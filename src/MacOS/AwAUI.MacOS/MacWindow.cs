using System;
using AwAUI.Graphics;
using AwAUI.MacOS.Interop;
using AwAUI.Platform;
using static AwAUI.MacOS.Interop.ObjC;

namespace AwAUI.MacOS;

/// <summary>窗口内容的呈现方式（见 <see cref="MacWindow.PresentationMode"/>）。</summary>
public enum MacPresentationMode
{
    /// <summary>软件呈现：框架渲染到内存帧缓冲，<c>drawRect:</c> 中经 CGImage 画到视图上。</summary>
    Framebuffer,

    /// <summary>GPU 呈现：视图上铺满一个 CAMetalLayer 子层，渲染后端直接向层的可绘制纹理渲染并呈现。</summary>
    MetalLayer,
}

/// <summary>
/// NSWindow + 自定义 NSView。表面（<see cref="Surface"/>）同时提供两种能力，呈现方式由渲染后端决定，应用不需要任何设置：
/// <list type="bullet">
/// <item>软件后端锁定 <see cref="IFramebufferSurface"/>：内存帧缓冲在 <c>drawRect:</c> 中经 CGImage 画到视图上（默认）。</item>
/// <item>GPU 后端读取 <see cref="IMetalLayerSurface.MetalLayer"/>：视图第一次被要求提供层时在自己的后备层上加一个铺满视图的 CAMetalLayer 子层，
/// 之后不再经过 <c>drawRect:</c>，帧由节拍直接驱动，后端向层的可绘制纹理渲染并呈现。
/// 层的 <c>contentsScale</c> 跟随 <c>backingScaleFactor</c>；可绘制尺寸由后端在 <see cref="IWindowEvents.OnResized"/> 之后经
/// <see cref="RenderTarget.Resize"/> 设置。拖动缩放期间层设为 <c>presentsWithTransaction</c>，每次尺寸变化同步出一帧，
/// 新内容与窗口的新尺寸在同一个 Core Animation 事务里上屏，因此不会拉伸或闪烁。
/// 不可见期间的帧可能因 <c>nextDrawable</c> 超时被丢弃；渲染目标经 <see cref="RenderTarget.LastFramePresented"/> 报告，
/// 宿主保留损伤并在下一个节拍整帧重画，窗口本身不再为此调用 <see cref="IWindowEvents.OnResized"/>。GPU 后端回退到软件（设备丢失）时再次锁定帧缓冲，视图恢复为 <c>drawRect:</c> 呈现。</item>
/// </list>
/// 不支持 CAMetalLayer 的系统上 <c>GetFeature&lt;IMetalLayerSurface&gt;()</c> 返回 null，GPU 后端因此不能为这个窗口创建渲染目标。
/// <para>
/// 帧节拍（design/18 帧调度）：<see cref="RequestFrame"/> 只登记需求并启动显示器同步的节拍源
/// （CADisplayLink，旧系统用 CVDisplayLink）；每个有需求的节拍出一帧（软件模式同步重绘视图，GPU 模式直接调用），
/// <see cref="IWindowEvents.OnFrame"/> 收到这一拍的预计呈现时间。没有需求、窗口被完全遮挡或最小化时节拍暂停，空闲应用不占 CPU。
/// </para>
/// </summary>
public sealed partial class MacWindow : WindowImpl
{
    readonly MacPlatform _platform;
    readonly IntPtr _window;
    readonly IntPtr _view;
    readonly IntPtr _delegate;
    readonly MacSurface _surface;
    static IntPtr s_colorSpace;

    readonly MacFrameClock? _clock;

    Size _lastSize;
    double _lastScale;
    bool _inFrame;          // 正在调用 OnFrame
    bool _inDrawRect;       // 正在 drawRect: 中（软件模式；之后会把帧缓冲画到视图上）
    bool _closed;

    // 呈现方式（只在主线程上访问）
    MacPresentationMode _mode;
    IntPtr _metalLayer;     // GPU 模式下铺满视图的 CAMetalLayer 子层（+1 引用）
    bool _metalHasFrame;    // GPU 模式下是否已经出过一帧
    bool _liveResize;       // 正在拖动缩放
    bool _framePosted;      // 没有节拍源时，已投递一次出帧

    // 帧调度状态（全部只在主线程上访问）
    bool _frameRequested;   // 框架请求了下一帧
    bool _occluded;         // 窗口不可见（被完全遮挡 / 最小化 / 未显示）
    bool _tickDraw;         // 当前 drawRect: 由节拍驱动
    bool _drawnInTick;      // 节拍内的 display 确实调用了 drawRect:
    double _tickTarget;     // 当前节拍的预计呈现时间（相对平台时间原点，秒）
    double _lastFrameTime = double.NegativeInfinity;
    double _refreshInterval;
    long _tickCount, _frameCount;
    PointerButtons _buttons;

    internal MacWindow(MacPlatform platform, WindowOptions options)
    {
        _platform = platform;
        var pool = objc_autoreleasePoolPush();
        ulong style = 1 | 2 | 4 | (options.Resizable ? 8UL : 0); // Titled | Closable | Miniaturizable | Resizable
        var rect = new NSRect(0, 0, options.Size.Width, options.Size.Height);
        _window = Send(Send(Class("NSWindow"), Sel("alloc")), Sel("initWithContentRect:styleMask:backing:defer:"), rect, style, 2UL, (byte)0);
        SendBool(_window, Sel("setReleasedWhenClosed:"), 0);
        SetTitle(options.Title);

        _view = Send(Send(Class("AwAUIView"), Sel("alloc")), Sel("initWithFrame:"), rect);
        Send(_window, Sel("setContentView:"), _view);
        SendBool(_window, Sel("setAcceptsMouseMovedEvents:"), 1);
        Send(_window, Sel("makeFirstResponder:"), _view);

        // 跟踪区域：鼠标进入/离开/移动（InVisibleRect 让它随视图大小自动更新）
        const ulong trackingOptions = 0x01 | 0x02 | 0x80 | 0x200;
        var tracking = Send(Send(Class("NSTrackingArea"), Sel("alloc")), Sel("initWithRect:options:owner:userInfo:"),
            new NSRect(0, 0, 0, 0), trackingOptions, _view, IntPtr.Zero);
        Send(_view, Sel("addTrackingArea:"), tracking);
        Release(tracking);

        _delegate = Send(Send(Class("AwAUIWindowDelegate"), Sel("alloc")), Sel("init"));
        Send(_window, Sel("setDelegate:"), _delegate);
        Send(_window, Sel("center"));

        _platform.Register(_view, this);
        _platform.Register(_delegate, this);
        _surface = new MacSurface(this);
        _lastSize = ClientSize;
        _lastScale = RenderScale;
        _clock = MacFrameClock.Create(_view);
        objc_autoreleasePoolPop(pool);
    }

    public override NativeSurface Surface => _surface;

    public override Size ClientSize
    {
        get
        {
            var b = SendRect(_view, Sel("bounds"));
            return new Size((float)b.Size.Width, (float)b.Size.Height);
        }
    }

    public override double RenderScale => SendDouble(_window, Sel("backingScaleFactor"));

    public override T? GetFeature<T>() where T : class => base.GetFeature<T>() ?? _platform.ReducedMotionPreference as T;

    /// <summary>
    /// 跟随系统设置「显示滚动条」：[NSScroller preferredScrollerStyle] 为 NSScrollerStyleOverlay（1）时用覆盖式，Legacy（0，接有鼠标或设为「始终」）时用经典式。
    /// </summary>
    public override ScrollbarStyle PreferredScrollbarStyle =>
        SendULong(Class("NSScroller"), Sel("preferredScrollerStyle")) == 0 ? ScrollbarStyle.Classic : ScrollbarStyle.Overlay;

    /// <summary>NSWindow* 句柄。</summary>
    public IntPtr Handle => _window;

    /// <summary>当前使用的帧节拍来源。</summary>
    public MacFrameClockKind FrameClock => _clock?.Kind ?? MacFrameClockKind.None;

    /// <summary>最近一次观测到的显示器刷新间隔；尚未收到节拍时为 <see cref="TimeSpan.Zero"/>。</summary>
    public TimeSpan RefreshInterval => Seconds(_refreshInterval);

    /// <summary>节拍源是否正在运行（诊断用：空闲时应为 false）。</summary>
    public bool IsFrameClockRunning => _clock?.Running ?? false;

    /// <summary>累计收到的节拍数与发出的 <see cref="IWindowEvents.OnFrame"/> 数（诊断用）。</summary>
    public long FrameClockTicks => _tickCount;

    /// <inheritdoc cref="FrameClockTicks"/>
    public long FramesDelivered => _frameCount;

    /// <summary>窗口当前是否被视为不可见（最小化、被完全遮挡等）。不可见时不出帧，请求会保留到重新可见。</summary>
    public bool IsOccluded => _occluded;

    /// <summary>当前的呈现方式：由渲染后端使用的表面能力决定（见类说明）。</summary>
    public MacPresentationMode PresentationMode => _mode;

    /// <summary>是否正在拖动缩放窗口（GPU 模式下此时每次尺寸变化都同步出帧并随窗口事务呈现）。</summary>
    public bool InLiveResize => _liveResize;

    public override void Show()
    {
        Send(_window, Sel("makeKeyAndOrderFront:"), IntPtr.Zero);
        _clock?.ScreenChanged(_window);
        // 遮挡状态在上屏后异步更新，这里不读取：以 windowDidChangeOcclusionState: 通知为准，初始视为可见
        UpdateClock();
    }

    public override void Close()
    {
        if (_closed) return;
        Send(_window, Sel("close")); // 触发 windowWillClose:
    }

    public override void SetTitle(string title)
    {
        var s = NewString(title);
        Send(_window, Sel("setTitle:"), s);
        Release(s);
    }

    public override void RequestFrame()
    {
        if (_closed) return;
        if (!_platform.Dispatcher.CheckAccess())
        {
            _platform.Dispatcher.Post(RequestFrame);
            return;
        }
        // 只登记需求：在 OnFrame 内再次请求（动画）时，下一个节拍继续出帧
        _frameRequested = true;
        UpdateClock();
    }

    /// <summary>让 AppKit 在下一个显示周期重新呈现帧缓冲（不触发 OnFrame）。</summary>
    void Invalidate()
    {
        if (_closed) return;
        if (!_platform.Dispatcher.CheckAccess())
        {
            _platform.Dispatcher.Post(Invalidate);
            return;
        }
        SendBool(_view, Sel("setNeedsDisplay:"), 1);
    }

    /// <summary>按「是否需要帧」与可见性启停节拍源。</summary>
    void UpdateClock()
    {
        if (_closed) return;
        bool want = _frameRequested && !_occluded;
        if (_clock == null)
        {
            // 没有显示器同步的节拍源：软件模式退回由 AppKit 显示周期合并的 setNeedsDisplay:；
            // GPU 模式没有 drawRect:，投递到消息循环出帧（nextDrawable 在可绘制对象用完时按显示器节拍阻塞，不会空转）
            if (!want) return;
            if (_mode == MacPresentationMode.MetalLayer || _inFrame)
            {
                if (_framePosted) return;
                _framePosted = true;
                _platform.Dispatcher.Post(() =>
                {
                    _framePosted = false;
                    if (_closed || !_frameRequested || _occluded) return;
                    if (_mode == MacPresentationMode.MetalLayer) DispatchFrame(tick: false);
                    else SendBool(_view, Sel("setNeedsDisplay:"), 1);
                });
            }
            else SendBool(_view, Sel("setNeedsDisplay:"), 1);
            return;
        }
        if (want) _clock.Start();
        else _clock.Stop();
    }

    void UpdateOcclusion()
    {
        if (_closed) return;
        // NSWindowOcclusionStateVisible = 1 << 1；最小化、被完全遮挡或未显示时该位为 0
        _occluded = (SendULong(_window, Sel("occlusionState")) & 2) == 0;
        // GPU 模式下不可见期间被丢掉的帧（nextDrawable 超时）由宿主按 RenderTarget.LastFramePresented 在下一个节拍整帧重画，
        // 重新可见时恢复节拍即可，不再需要经 OnResized 强制重呈现
        UpdateClock();
    }

    /// <summary>节拍到达（主线程）。</summary>
    void OnClockTick(IntPtr arg)
    {
        if (_closed || _clock == null) return;
        if (!_clock.ReadTick(arg, out double target, out double interval)) return;
        _tickCount++;
        if (interval > 0) _refreshInterval = interval;
        if (!_frameRequested || _occluded)
        {
            _clock.Stop(); // 没有需求：暂停，应用静止
            return;
        }

        _tickTarget = target - _platform.Epoch;
        if (_mode == MacPresentationMode.MetalLayer)
        {
            // GPU 模式：视图没有 drawRect:，直接出帧；后端在 OnFrame 内向 CAMetalLayer 呈现
            DispatchFrame(tick: true);
            return;
        }
        _tickDraw = true;
        _drawnInTick = false;
        try { Send(_view, Sel("display")); } // 同步调用 drawRect:
        finally { _tickDraw = false; }

        if (!_drawnInTick && !_closed)
        {
            // 视图当前不能绘制（例如窗口尚未上屏）：停止空转，交给 AppKit 在可绘制时调用 drawRect:
            _clock.Stop();
            SendBool(_view, Sel("setNeedsDisplay:"), 1);
        }
    }

    public override void SetCursor(CursorShape cursor)
    {
        string sel = cursor switch
        {
            CursorShape.IBeam => "IBeamCursor",
            CursorShape.Hand => "pointingHandCursor",
            CursorShape.Crosshair => "crosshairCursor",
            CursorShape.ResizeHorizontal => "resizeLeftRightCursor",
            CursorShape.ResizeVertical => "resizeUpDownCursor",
            CursorShape.NotAllowed => "operationNotAllowedCursor",
            _ => "arrowCursor",
        };
        Send(Send(Class("NSCursor"), Sel(sel)), Sel("set"));
    }

    // ---------------------------------------------------------------- 原生回调

    internal bool ShouldClose() => Events?.OnCloseRequested() ?? true;

    internal void DrawRect()
    {
        _drawnInTick = _tickDraw;
        if (_mode != MacPresentationMode.Framebuffer) return; // GPU 模式下重绘策略为 Never，防御性检查
        _inDrawRect = true;
        try
        {
            DispatchFrame(_tickDraw);
            if (_closed || _mode != MacPresentationMode.Framebuffer) return;
            BlitFramebuffer();
        }
        finally
        {
            _inDrawRect = false;
        }
    }

    /// <summary>
    /// 两种呈现方式共用的出帧逻辑：先检测尺寸与缩放变化（<see cref="IWindowEvents.OnResized"/>），再在需要时调用 <see cref="IWindowEvents.OnFrame"/>。
    /// 节拍驱动、有待处理的请求、尺寸变化（拖动缩放时 AppKit 同步调用）或还没有内容时出帧；
    /// 否则只是系统要求重新显示（例如暴露），软件模式直接呈现已有的帧缓冲。
    /// </summary>
    void DispatchFrame(bool tick)
    {
        // 每帧一个自动释放池：GPU 模式的帧不在 AppKit 的绘制周期里（节拍 / 投递的消息直接调用），
        // 帧内自动释放的对象（Metal 的描述符、可绘制对象等）在这一帧结束时就释放，不依赖消息循环何时排空外层的池
        var pool = objc_autoreleasePoolPush();
        try
        {
            DispatchFrameCore(tick);
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    void DispatchFrameCore(bool tick)
    {
        var size = ClientSize;
        double scale = RenderScale;
        bool resized = size != _lastSize || scale != _lastScale;
        if (resized)
        {
            _lastSize = size;
            _lastScale = scale;
            UpdateMetalLayerGeometry();
            Events?.OnResized(size, scale);
        }

        bool noContent = _mode == MacPresentationMode.MetalLayer ? !_metalHasFrame : _surface.Framebuffer == null;
        if (!(tick || _frameRequested || resized || noContent)) return;
        _frameRequested = false; // 先清除：OnFrame 内的请求留给下一个节拍
        double t = tick ? _tickTarget : HostTime.Now - _platform.Epoch;
        if (t < _lastFrameTime) t = _lastFrameTime; // 保证单调
        _lastFrameTime = t;
        _frameCount++;
        _inFrame = true;
        try { Events?.OnFrame(Seconds(t)); }
        finally { _inFrame = false; }
        if (_mode == MacPresentationMode.MetalLayer) _metalHasFrame = true;
    }

    /// <summary>软件模式：把帧缓冲画到视图上（在 drawRect: 中）。</summary>
    void BlitFramebuffer()
    {
        var size = ClientSize;
        double scale = RenderScale;
        var fb = _surface.Framebuffer;
        if (fb == null) return;
        var nsContext = Send(Class("NSGraphicsContext"), Sel("currentContext"));
        if (nsContext == IntPtr.Zero) return;
        var cg = Send(nsContext, Sel("CGContext"));

        var provider = CG.CGDataProviderCreateWithData(IntPtr.Zero, fb.Address, (UIntPtr)(ulong)(fb.RowBytes * fb.Height), IntPtr.Zero);
        var image = CG.CGImageCreate((UIntPtr)(uint)fb.Width, (UIntPtr)(uint)fb.Height, (UIntPtr)8u, (UIntPtr)32u,
            (UIntPtr)(uint)fb.RowBytes, SrgbColorSpace, CG.AlphaPremultipliedLast, provider, IntPtr.Zero, 0, 0);
        CG.CGContextSetInterpolationQuality(cg, 1); // 不插值：帧缓冲与设备像素一一对应
        CG.CGContextSetBlendMode(cg, CG.BlendModeCopy);
        double w = fb.Width / scale, h = fb.Height / scale;
        // 视图坐标原点在左下：让帧缓冲顶边与视图顶边对齐
        CG.CGContextDrawImage(cg, new NSRect(0, size.Height - h, w, h), image);
        CG.CGImageRelease(image);
        CG.CGDataProviderRelease(provider);
    }

    internal void HandleEvent(IntPtr sel, IntPtr e)
    {
        if (sel == Sel("windowWillClose:")) { OnWillClose(); return; }
        if (sel == Sel("windowDidBecomeKey:")) { Events?.OnFocusChanged(true); return; }
        if (sel == Sel("windowDidResignKey:")) { Events?.OnFocusChanged(false); return; }
        if (sel == Sel(MacFrameClock.TickSelector)) { OnClockTick(e); return; }
        // 编辑命令（参数是 SEL 而不是对象）由框架按键处理；空实现避免 NSResponder 的提示音
        if (sel == Sel("doCommandBySelector:")) return;
        if (sel == Sel("windowDidChangeBackingProperties:"))
        {
            // 软件模式：下一帧在 drawRect: 中检测到新的缩放；GPU 模式：立即更新层并出帧
            if (_mode == MacPresentationMode.Framebuffer) RequestFrame();
            else OnGeometryChanged();
            return;
        }
        if (sel == Sel("windowDidResize:")) { if (_mode == MacPresentationMode.MetalLayer) OnGeometryChanged(); return; }
        if (sel == Sel("windowWillStartLiveResize:")) { SetLiveResize(true); return; }
        if (sel == Sel("windowDidEndLiveResize:")) { SetLiveResize(false); return; }
        if (sel == Sel("windowDidChangeOcclusionState:")) { UpdateOcclusion(); return; }
        if (sel == Sel("windowDidChangeScreen:")) { _clock?.ScreenChanged(_window); return; }
        if (Events == null || e == IntPtr.Zero) return;

        var time = Seconds(SendDouble(e, Sel("timestamp")));
        var mods = KeyMap.Modifiers(SendULong(e, Sel("modifierFlags")));

        if (sel == Sel("keyDown:") || sel == Sel("keyUp:"))
        {
            HandleKeyEvent(e, sel == Sel("keyDown:"), mods, time);
            return;
        }
        if (sel == Sel("flagsChanged:")) return;

        var location = SendPoint(e, Sel("locationInWindow"));
        var local = SendPoint(_view, Sel("convertPoint:fromView:"), location, IntPtr.Zero);
        var pos = new Point((float)local.X, (float)(ClientSize.Height - local.Y));

        if (sel == Sel("scrollWheel:"))
        {
            // 触控板：精确增量 + 手势相位（phase）与系统惯性相位（momentumPhase），框架直接跟随，不叠加自己的惯性
            bool precise = SendByte(e, Sel("hasPreciseScrollingDeltas")) != 0;
            MacWheel.Translate(SendDouble(e, Sel("scrollingDeltaX")), SendDouble(e, Sel("scrollingDeltaY")), precise,
                out float dx, out float dy, out var mode);
            var phase = MacWheel.MapPhase(SendULong(e, Sel("phase")));
            var momentum = MacWheel.MapPhase(SendULong(e, Sel("momentumPhase")));
            Events.OnPointer(RawPointerEvent.CreateWheel(pos, dx, dy, mode, precise, phase, momentum, _buttons, mods, time));
            return;
        }

        RawPointerEventType type;
        var changed = PointerButtons.None;
        if (sel == Sel("mouseDown:")) { type = RawPointerEventType.Down; changed = PointerButtons.Left; }
        else if (sel == Sel("mouseUp:")) { type = RawPointerEventType.Up; changed = PointerButtons.Left; }
        else if (sel == Sel("rightMouseDown:")) { type = RawPointerEventType.Down; changed = PointerButtons.Right; }
        else if (sel == Sel("rightMouseUp:")) { type = RawPointerEventType.Up; changed = PointerButtons.Right; }
        else if (sel == Sel("otherMouseDown:")) { type = RawPointerEventType.Down; changed = OtherButton(e); }
        else if (sel == Sel("otherMouseUp:")) { type = RawPointerEventType.Up; changed = OtherButton(e); }
        else if (sel == Sel("mouseEntered:")) type = RawPointerEventType.Enter;
        else if (sel == Sel("mouseExited:")) type = RawPointerEventType.Leave;
        else type = RawPointerEventType.Move; // mouseMoved / *Dragged

        if (type == RawPointerEventType.Down) _buttons |= changed;
        else if (type == RawPointerEventType.Up) _buttons &= ~changed;
        float pressure = type == RawPointerEventType.Move && _buttons == PointerButtons.None ? 0f : 0.5f;
        Events.OnPointer(new RawPointerEvent(type, PointerType.Mouse, 0, pos, changed, _buttons, mods, time, pressure));
    }

    static PointerButtons OtherButton(IntPtr e) => (long)SendULong(e, Sel("buttonNumber")) switch
    {
        2 => PointerButtons.Middle,
        3 => PointerButtons.X1,
        4 => PointerButtons.X2,
        _ => PointerButtons.None,
    };

    // TimeSpan.FromSeconds 在 .NET Framework 上会舍入到毫秒，这里按 tick 精确换算
    static TimeSpan Seconds(double s) => TimeSpan.FromTicks((long)(s * TimeSpan.TicksPerSecond));

    static bool IsPrintable(string text)
    {
        foreach (var c in text)
            if (c < 0x20 || c == 0x7F || (c >= 0xF700 && c <= 0xF8FF)) return false; // 控制字符与功能键私用区
        return true;
    }

    /// <summary>sRGB 色彩空间（进程内共享，不释放）。帧缓冲与 CAMetalLayer 的内容都按 sRGB 解释。</summary>
    static IntPtr SrgbColorSpace
    {
        get
        {
            if (s_colorSpace == IntPtr.Zero)
            {
                var srgb = CG.GetConstant("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics", "kCGColorSpaceSRGB");
                s_colorSpace = srgb != IntPtr.Zero ? CG.CGColorSpaceCreateWithName(srgb) : CG.CGColorSpaceCreateDeviceRGB();
            }
            return s_colorSpace;
        }
    }

    /// <summary>
    /// 诊断：截取窗口内容区经窗口服务器合成后的像素（RGBA8 预乘，设备像素，转换回 sRGB），即用户实际看到的画面。
    /// 用于在测试与示例里自检两种呈现方式的真实输出。用 CGWindowListCreateImage 截取本进程自己的窗口（不需要屏幕录制权限）；
    /// 该 API 在新系统上已标记为弃用，不可用、窗口不在屏幕上或截取失败时返回 null。只能在主线程上调用。
    /// </summary>
    public Bitmap? CaptureContent()
    {
        if (_closed) return null;
        var pool = objc_autoreleasePoolPush();
        try
        {
            uint number = (uint)SendULong(_window, Sel("windowNumber"));
            IntPtr image;
            try
            {
                // CGRectNull + kCGWindowListOptionIncludingWindow；kCGWindowImageBoundsIgnoreFraming | kCGWindowImageBestResolution
                image = CG.CGWindowListCreateImage(new NSRect(double.PositiveInfinity, double.PositiveInfinity, 0, 0), 8, number, 1 | 8);
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
            if (image == IntPtr.Zero) return null;
            try
            {
                int w = (int)(ulong)CG.CGImageGetWidth(image), h = (int)(ulong)CG.CGImageGetHeight(image);
                var content = PixelSize.FromSize(ClientSize, RenderScale);
                if (w <= 0 || h <= 0 || content.IsEmpty) return null;
                // 截图包含标题栏：内容区在图像底部，宽度相同
                int cw = Math.Min(w, content.Width), ch = Math.Min(h, content.Height);
                var result = new Bitmap(new PixelSize(cw, ch));
                var ctx = CG.CGBitmapContextCreate(result.Address, (UIntPtr)(uint)cw, (UIntPtr)(uint)ch, (UIntPtr)8u, (UIntPtr)(uint)result.RowBytes,
                    SrgbColorSpace, CG.AlphaPremultipliedLast);
                if (ctx == IntPtr.Zero)
                {
                    result.Dispose();
                    return null;
                }
                CG.CGContextSetBlendMode(ctx, CG.BlendModeCopy);
                // 位图上下文原点在左下：整张截图的底边对齐上下文底边，标题栏落在上下文之外
                CG.CGContextDrawImage(ctx, new NSRect(0, 0, w, h), image);
                CG.CGContextRelease(ctx);
                return result;
            }
            finally
            {
                CG.CGImageRelease(image);
            }
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    // ---------------------------------------------------------------- 呈现方式

    /// <summary>当前系统能否创建 CAMetalLayer（QuartzCore 随 AppKit 加载）。</summary>
    internal static bool MetalLayerSupported => s_metalLayerSupported ??= TryClass("CAMetalLayer") != IntPtr.Zero;
    static bool? s_metalLayerSupported;

    /// <summary>
    /// 切换到 GPU 呈现并返回 CAMetalLayer（渲染后端经 <see cref="IMetalLayerSurface.MetalLayer"/> 调用）。
    /// 层作为子层铺满视图的后备层（视图本身保持普通的层支持视图，事件与软件模式完全相同；切回软件时只需移除子层）；
    /// 视图的 <c>layerContentsRedrawPolicy</c> 设为 Never，AppKit 不再为它调用 <c>drawRect:</c>。层只创建一次，切回软件后再次调用会重新创建。
    /// </summary>
    IntPtr EnterMetalLayerMode()
    {
        if (_closed) throw new ObjectDisposedException(nameof(MacWindow));
        if (!_platform.Dispatcher.CheckAccess()) throw new InvalidOperationException("The Metal layer must be requested on the main thread.");
        if (_metalLayer != IntPtr.Zero) return _metalLayer;
        var cls = TryClass("CAMetalLayer");
        if (cls == IntPtr.Zero) throw new NotSupportedException("CAMetalLayer is not available on this system.");
        var pool = objc_autoreleasePoolPush();
        try
        {
            SendBool(_view, Sel("setWantsLayer:"), 1);
            var host = Send(_view, Sel("layer"));
            if (host == IntPtr.Zero) throw new NotSupportedException("The view has no backing layer.");
            var layer = Retain(Send(cls, Sel("layer")));
            if (layer == IntPtr.Zero) throw new NotSupportedException("Could not create a CAMetalLayer.");
            // 可绘制尺寸与层尺寸暂时不一致时（尺寸变化后、新帧呈现前）内容贴住左上角，不拉伸
            var gravity = CG.GetConstant(QuartzCore, "kCAGravityTopLeft");
            if (gravity != IntPtr.Zero) Send(layer, Sel("setContentsGravity:"), gravity);
            if (_liveResize) SendBool(layer, Sel("setPresentsWithTransaction:"), 1);
            // 与软件模式的 CGImage 一样标记为 sRGB：未标记的层按显示器色彩空间解释（P3 屏上颜色偏艳，两种模式不一致）
            Send(layer, Sel("setColorspace:"), SrgbColorSpace);
            _savedRedrawPolicy = (long)SendULong(_view, Sel("layerContentsRedrawPolicy"));
            Send(_view, Sel("setLayerContentsRedrawPolicy:"), 0L); // NSViewLayerContentsRedrawNever
            Send(host, Sel("setContents:"), IntPtr.Zero); // 软件模式画过的内容不能从透明像素后面透出来
            _metalLayer = layer;
            UpdateMetalLayerGeometry();
            Send(host, Sel("addSublayer:"), layer);
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
        _mode = MacPresentationMode.MetalLayer;
        _metalHasFrame = false;
        // 软件模式留下的帧缓冲不再需要
        _surface.Dispose();
        return _metalLayer;
    }

    long _savedRedrawPolicy;

    /// <summary>让 CAMetalLayer 铺满视图并跟随缩放（关闭隐式动画：尺寸变化立即生效，不做 0.25 s 的过渡）。</summary>
    void UpdateMetalLayerGeometry()
    {
        if (_metalLayer == IntPtr.Zero) return;
        var tx = Class("CATransaction");
        Send(tx, Sel("begin"));
        SendBool(tx, Sel("setDisableActions:"), 1);
        Send(_metalLayer, Sel("setFrame:"), SendRect(_view, Sel("bounds")));
        SendDoubleArg(_metalLayer, Sel("setContentsScale:"), RenderScale);
        Send(tx, Sel("commit"));
    }

    /// <summary>切回软件呈现（软件后端锁定帧缓冲时调用；例如 GPU 后端因设备丢失回退）。</summary>
    void EnterFramebufferMode()
    {
        if (_mode == MacPresentationMode.Framebuffer) return;
        _mode = MacPresentationMode.Framebuffer;
        var layer = _metalLayer;
        _metalLayer = IntPtr.Zero;
        if (layer != IntPtr.Zero)
        {
            var tx = Class("CATransaction");
            Send(tx, Sel("begin"));
            SendBool(tx, Sel("setDisableActions:"), 1);
            Send(layer, Sel("removeFromSuperlayer"));
            Send(tx, Sel("commit"));
            Release(layer); // 交换链若还持有它，由交换链释放时最终销毁
        }
        if (_closed) return;
        Send(_view, Sel("setLayerContentsRedrawPolicy:"), _savedRedrawPolicy);
        SendBool(_view, Sel("setNeedsDisplay:"), 1);
    }

    /// <summary>GPU 模式下尺寸或缩放变化（windowDidResize: / windowDidChangeBackingProperties:）。软件模式由 AppKit 调用 drawRect: 处理。</summary>
    void OnGeometryChanged()
    {
        if (_closed || _mode != MacPresentationMode.MetalLayer) return;
        UpdateMetalLayerGeometry();
        // 同步出帧。拖动缩放期间层 presentsWithTransaction，新帧与窗口的新尺寸在同一个事务里上屏；不可见时留到重新可见。
        // 通知在 OnFrame 内部到达（帧回调里改了窗口尺寸等）时不能重入出帧：登记请求，下一个节拍检测到尺寸变化
        if (_occluded || _inFrame) { RequestFrame(); return; }
        if (ClientSize != _lastSize || RenderScale != _lastScale) DispatchFrame(tick: false);
    }

    void SetLiveResize(bool active)
    {
        if (_closed || _liveResize == active) return;
        _liveResize = active;
        if (_metalLayer != IntPtr.Zero) SendBool(_metalLayer, Sel("setPresentsWithTransaction:"), active ? (byte)1 : (byte)0);
        if (!active) RequestFrame(); // 结束后按常规节拍再出一帧
    }

    const string QuartzCore = "/System/Library/Frameworks/QuartzCore.framework/QuartzCore";

    void OnWillClose()
    {
        if (_closed) return;
        _closed = true;
        _clock?.Dispose(); // invalidate 显示链接，解除它对视图的持有
        _platform.Unregister(_view);
        _platform.Unregister(_delegate);
        Send(_window, Sel("setDelegate:"), IntPtr.Zero);
        Events?.OnClosed();
        _surface.Dispose();
        Release(_metalLayer); // 交换链（若还在）持有自己的引用
        _metalLayer = IntPtr.Zero;
        _platform.OnWindowClosed(this);
    }

    /// <summary>
    /// 窗口表面：同时提供软件呈现的 <see cref="IFramebufferSurface"/> 与 GPU 呈现的 <see cref="IMetalLayerSurface"/>，
    /// 后端第一次使用哪种能力，窗口就切换到哪种呈现方式（见 <see cref="MacWindow"/> 的说明）。
    /// </summary>
    sealed class MacSurface : NativeSurface, IFramebufferSurface, IMetalLayerSurface, IDisposable
    {
        readonly MacWindow _owner;
        public Bitmap? Framebuffer;

        public MacSurface(MacWindow owner) => _owner = owner;

        public override PixelSize Size => PixelSize.FromSize(_owner.ClientSize, _owner.RenderScale);
        public override double Scale => _owner.RenderScale;

        public override T? GetFeature<T>() where T : class
        {
            if (typeof(T) == typeof(IMetalLayerSurface) && !MetalLayerSupported) return null;
            return this as T;
        }

        /// <summary>读取即切换到 GPU 呈现（视图上加一个 CAMetalLayer 子层）。只能在主线程上调用。</summary>
        public IntPtr MetalLayer => _owner.EnterMetalLayerMode();

        public ILockedFramebuffer Lock()
        {
            _owner.EnterFramebufferMode();
            var size = Size;
            if (size.IsEmpty) size = new PixelSize(1, 1);
            if (Framebuffer == null || Framebuffer.Size != size)
            {
                Framebuffer?.Dispose();
                Framebuffer = new Bitmap(size);
            }
            return new Locked(Framebuffer, _owner);
        }

        public void Dispose()
        {
            Framebuffer?.Dispose();
            Framebuffer = null;
        }

        sealed class Locked : ILockedFramebuffer
        {
            readonly Bitmap _bitmap;
            readonly MacWindow _owner;
            public Locked(Bitmap bitmap, MacWindow owner) { _bitmap = bitmap; _owner = owner; }
            public IntPtr Address => _bitmap.Address;
            public PixelSize Size => _bitmap.Size;
            public int RowBytes => _bitmap.RowBytes;
            public PixelFormat Format => _bitmap.Format;

            // 在 drawRect: 之外绘制时（包括 GPU 回退到软件后从节拍里绘制），只需让 AppKit 在下一个显示周期呈现新内容（不再触发 OnFrame）
            public void Dispose()
            {
                if (!_owner._inDrawRect) _owner.Invalidate();
            }
        }
    }
}
