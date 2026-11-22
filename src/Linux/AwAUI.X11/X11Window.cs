using System;
using System.Collections.Generic;
using System.Text;
using AwAUI.Graphics;
using AwAUI.Platform;
using AwAUI.X11.Interop;
using static AwAUI.X11.Interop.Xlib;

namespace AwAUI.X11;

/// <summary>
/// X11 顶层窗口。M0 采用软件呈现：框架渲染到 <see cref="X11Framebuffer"/>，在帧末与 Expose 时经 XPutImage / XShmPutImage 上屏。
/// <para>
/// 帧节拍：<see cref="RequestFrame"/> 只登记需求，由 <see cref="X11Platform"/> 的循环按单调时钟（默认 60Hz）发出
/// <see cref="IWindowEvents.OnFrame"/>；窗口未映射（最小化、未显示）时暂停。没有需求时循环完全阻塞在 poll 上。
/// </para>
/// 所有成员只能在平台线程上调用（<see cref="RequestFrame"/> 除外，它会转投到平台线程）。
/// </summary>
public sealed unsafe class X11Window : WindowImpl, IWindowChrome
{
    /// <summary>Linux 桌面使用常驻的经典滚动条。</summary>
    public override ScrollbarStyle PreferredScrollbarStyle => ScrollbarStyle.Classic;

    readonly X11Platform _platform;
    readonly IntPtr _display;
    readonly ulong _window;
    readonly IntPtr _gc;
    readonly X11Framebuffer _framebuffer;
    readonly X11Surface _surface;
    IntPtr _ic;
    IntPtr _icCallbacks;
    bool _onTheSpot;
    X11TextInput? _textInput;

    PixelSize _clientPx;
    bool _closed;
    bool _mapped;
    bool _focused;

    // 帧调度
    bool _frameRequested;
    bool _inFrame;
    bool _presentPending;

    // Expose 累积的脏矩形（设备像素）
    int _exposeX0 = int.MaxValue, _exposeY0 = int.MaxValue, _exposeX1 = int.MinValue, _exposeY1 = int.MinValue;

    // 指针
    PointerButtons _buttons;
    bool _inside;
    bool _leavePending;    // 按住按钮拖出窗口：离开推迟到全部按钮抬起（与 Win32 的捕获语义一致）
    int _lastRootX, _lastRootY; // 最近一次指针事件的根窗口坐标（_NET_WM_MOVERESIZE）

    // 窗口模型（design/04「窗口状态与几何」）
    Point _position;
    WindowState _windowState;
    bool _topmost;
    bool _startMinimized;                 // 首次映射后再发 WM_CHANGE_STATE IconicState
    readonly HashSet<ulong> _preMapStates = new(); // 映射前写在 _NET_WM_STATE 属性里的状态（映射后改为 ClientMessage）

    // 键盘
    readonly bool[] _keyDown = new bool[256];

    internal X11Window(X11Platform platform, WindowOptions options)
    {
        _platform = platform;
        _display = platform.Display;
        var visual = platform.Visual;
        double scale = platform.Scale;
        var px = PixelSize.FromSize(options.Size, scale);
        if (px.IsEmpty) px = new PixelSize(Math.Max(1, px.Width), Math.Max(1, px.Height));
        _clientPx = px;

        // 首次显示的几何（design/04）：Position / StartupLocation 在主显示器工作区内换算为根窗口坐标
        var start = X11WindowGeometry.ResolveStartupPosition(options, platform.Screens.Primary);
        _position = start;
        int startX = X11WindowGeometry.ToPixelCoordinate(start.X, scale);
        int startY = X11WindowGeometry.ToPixelCoordinate(start.Y, scale);

        // 初始状态 / 置顶 / 任务栏可见性：映射前写入 _NET_WM_STATE 属性（EWMH 要求映射后改用 ClientMessage）
        _windowState = options.State;
        _topmost = options.Topmost;
        _startMinimized = options.State == WindowState.Minimized;
        if (options.State == WindowState.Maximized)
        {
            _preMapStates.Add(platform.NetWmStateMaximizedVert);
            _preMapStates.Add(platform.NetWmStateMaximizedHorz);
        }
        else if (options.State == WindowState.FullScreen)
        {
            _preMapStates.Add(platform.NetWmStateFullscreen);
        }
        if (options.Topmost) _preMapStates.Add(platform.NetWmStateAbove);
        if (!options.ShowInTaskbar)
        {
            _preMapStates.Add(platform.NetWmStateSkipTaskbar);
            _preMapStates.Add(platform.NetWmStateSkipPager);
        }

        var attributes = new XSetWindowAttributes
        {
            background_pixmap = 0,           // None：服务器不清背景，缩放时不闪烁
            border_pixel = 0,                // 非默认视觉时必须显式给出，否则 BadMatch
            bit_gravity = NorthWestGravity,  // 缩放时保留左上角内容，等待新帧
            colormap = visual.Colormap,
            event_mask = KeyPressMask | KeyReleaseMask | ButtonPressMask | ButtonReleaseMask | PointerMotionMask
                | EnterWindowMask | LeaveWindowMask | ExposureMask | StructureNotifyMask | FocusChangeMask | PropertyChangeMask,
        };
        _window = XCreateWindow(_display, platform.Root, startX, startY, (uint)px.Width, (uint)px.Height, 0, visual.Depth, InputOutput,
            visual.Visual, CWBackPixmap | CWBorderPixel | CWBitGravity | CWEventMask | CWColormap, &attributes);
        if (_window == 0) throw new InvalidOperationException("XCreateWindow failed.");

        ulong deleteWindow = platform.WmDeleteWindow;
        XSetWMProtocols(_display, _window, &deleteWindow, 1);
        SetClassHint();
        SetTitle(options.Title);
        ApplyNormalHints(options, px, startX, startY);
        if (_preMapStates.Count > 0) WritePreMapStates();
        if (options.Icon is Bitmap icon) SetIcon(icon);
        if (options.Decorations == WindowDecorations.None)
        {
            // Motif 提示：窗口管理器不画标题栏与边框（必须在 map 之前设置）。未识别的 WM 仍可能加边框。
            long* hints = stackalloc long[5];
            hints[0] = MwmHintsDecorations; // flags
            hints[1] = 0;                   // functions
            hints[2] = 0;                   // decorations = 0
            hints[3] = 0;                   // input_mode
            hints[4] = 0;                   // status
            XChangeProperty(_display, _window, _platform.MotifWmHints, _platform.MotifWmHints, 32, PropModeReplace,
                (byte*)hints, 5);
        }

        _gc = XCreateGC(_display, _window, 0, null);
        _ic = platform.CreateInputContext(_window, out _icCallbacks, out _onTheSpot);
        _framebuffer = new X11Framebuffer(platform, _window, _gc);
        _surface = new X11Surface(this);
        XFlush(_display);
    }

    public override NativeSurface Surface => _surface;

    public override Size ClientSize => new((float)(_clientPx.Width / RenderScale), (float)(_clientPx.Height / RenderScale));

    public override double RenderScale => _platform.Scale;

    public override T? GetFeature<T>() where T : class =>
        base.GetFeature<T>() ?? _platform.AppearancePreference as T ?? _platform.ReducedMotionPreference as T;

    // ------------------------------------------------------------ 窗口模型（design/04）

    /// <summary>窗口左上角在根窗口中的逻辑坐标；首次映射使用请求的位置，之后跟随 ConfigureNotify。</summary>
    public override Point Position => _position;

    public override WindowState WindowState => _windowState;

    public override bool Topmost
    {
        get => _topmost;
        set => SetTopmost(value);
    }

    public override bool IsActive => _focused;

    /// <summary>桌面窗口没有刘海或软键盘占位。</summary>
    public override Thickness SafeAreaInsets => Thickness.Zero;

    public override Screens Screens => _platform.Screens;

    public override StorageProvider StorageProvider => _platform.StorageProvider;

    public override Launcher Launcher => _platform.Launcher;

    public override void SetPosition(Point position)
    {
        if (_closed) return;
        int x = X11WindowGeometry.ToPixelCoordinate(position.X, RenderScale);
        int y = X11WindowGeometry.ToPixelCoordinate(position.Y, RenderScale);
        _position = position;
        XMoveWindow(_display, _window, x, y);
        XFlush(_display);
    }

    public override void SetWindowState(WindowState state)
    {
        if (_closed) return;
        switch (state)
        {
            case WindowState.Normal:
                _startMinimized = false;
                if (_mapped)
                {
                    RequestNetWmState(_platform.NetWmStateFullscreen, false);
                    RequestNetWmState(_platform.NetWmStateMaximizedVert, false);
                    RequestNetWmState(_platform.NetWmStateMaximizedHorz, false);
                    if (_windowState == WindowState.Minimized)
                    {
                        XMapWindow(_display, _window);
                        Activate();
                    }
                }
                else
                {
                    _preMapStates.Remove(_platform.NetWmStateFullscreen);
                    _preMapStates.Remove(_platform.NetWmStateMaximizedVert);
                    _preMapStates.Remove(_platform.NetWmStateMaximizedHorz);
                    WritePreMapStates();
                }
                UpdateState(WindowState.Normal);
                break;
            case WindowState.Minimized:
                if (_mapped) SendRootClientMessage(_platform.WmChangeState, IconicState, 0, 0, 0, 0);
                else _startMinimized = true;
                UpdateState(WindowState.Minimized);
                break;
            case WindowState.Maximized:
                _startMinimized = false;
                if (_mapped)
                {
                    RequestNetWmState(_platform.NetWmStateFullscreen, false);
                    RequestNetWmState(_platform.NetWmStateMaximizedVert, true);
                    RequestNetWmState(_platform.NetWmStateMaximizedHorz, true);
                }
                else
                {
                    _preMapStates.Remove(_platform.NetWmStateFullscreen);
                    _preMapStates.Add(_platform.NetWmStateMaximizedVert);
                    _preMapStates.Add(_platform.NetWmStateMaximizedHorz);
                    WritePreMapStates();
                }
                UpdateState(WindowState.Maximized);
                break;
            case WindowState.FullScreen:
                _startMinimized = false;
                if (_mapped)
                {
                    RequestNetWmState(_platform.NetWmStateMaximizedVert, false);
                    RequestNetWmState(_platform.NetWmStateMaximizedHorz, false);
                    RequestNetWmState(_platform.NetWmStateFullscreen, true);
                }
                else
                {
                    _preMapStates.Remove(_platform.NetWmStateMaximizedVert);
                    _preMapStates.Remove(_platform.NetWmStateMaximizedHorz);
                    _preMapStates.Add(_platform.NetWmStateFullscreen);
                    WritePreMapStates();
                }
                UpdateState(WindowState.FullScreen);
                break;
        }
        XFlush(_display);
    }

    public override void Activate()
    {
        if (_closed) return;
        // EWMH：l0 = 来源（1 = 应用），l1 = 时间戳（选择协议要求真实时间，没有输入事件时为 0）
        SendRootClientMessage(_platform.NetActiveWindow, NetWmMoveresizeSourceApplication, (long)_platform.LastEventTime, 0, 0, 0);
        XFlush(_display);
    }

    void SetTopmost(bool value)
    {
        if (_closed) return;
        if (_topmost == value) return;
        _topmost = value;
        if (_mapped)
        {
            RequestNetWmState(_platform.NetWmStateAbove, value);
            XFlush(_display);
        }
        else
        {
            if (value) _preMapStates.Add(_platform.NetWmStateAbove);
            else _preMapStates.Remove(_platform.NetWmStateAbove);
            WritePreMapStates();
        }
    }

    void RequestNetWmState(ulong atom, bool add)
    {
        if (atom == 0) return;
        SendRootClientMessage(_platform.NetWmState, add ? NetWmStateAdd : NetWmStateRemove, (long)atom, 0,
            NetWmMoveresizeSourceApplication, 0);
    }

    void UpdateState(WindowState state)
    {
        if (_windowState == state) return;
        _windowState = state;
        (Events as IWindowStateEvents)?.OnWindowStateChanged(state);
    }

    void RefreshStateFromProperties()
    {
        var atoms = ReadAtomList(_platform.NetWmState);
        bool fullscreen = ContainsAtom(atoms, _platform.NetWmStateFullscreen);
        bool maximized = ContainsAtom(atoms, _platform.NetWmStateMaximizedVert)
            && ContainsAtom(atoms, _platform.NetWmStateMaximizedHorz);
        _topmost = ContainsAtom(atoms, _platform.NetWmStateAbove);

        int? wmState = ReadWmState();
        WindowState state;
        if (wmState == IconicState) state = WindowState.Minimized;
        else if (fullscreen) state = WindowState.FullScreen;
        else if (maximized) state = WindowState.Maximized;
        else state = WindowState.Normal;
        UpdateState(state);
    }

    static bool ContainsAtom(ulong[] atoms, ulong atom)
    {
        if (atom == 0) return false;
        foreach (var value in atoms)
            if (value == atom) return true;
        return false;
    }

    /// <summary>读取窗口上的 32 位 ATOM 列表属性（最多 64 项）；属性不存在或格式不符时返回空数组。</summary>
    ulong[] ReadAtomList(ulong property)
    {
        ulong actualType, nitems, bytesAfter;
        int format;
        byte* data = null;
        int status = XGetWindowProperty(_display, _window, property, 0, 64, 0, XA_ATOM,
            &actualType, &format, &nitems, &bytesAfter, &data);
        try
        {
            if (status != 0 || data == null || format != 32 || nitems == 0) return Array.Empty<ulong>();
            int n = (int)Math.Min(nitems, 64UL);
            var values = new ulong[n];
            long* p = (long*)data;
            for (int i = 0; i < n; i++) values[i] = (ulong)p[i];
            return values;
        }
        finally
        {
            if (data != null) XFree(data);
        }
    }

    /// <summary>读取 ICCCM 的 <c>WM_STATE</c>（[state, icon_window]）；窗口管理器未设置时为 null。</summary>
    int? ReadWmState()
    {
        ulong actualType, nitems, bytesAfter;
        int format;
        byte* data = null;
        int status = XGetWindowProperty(_display, _window, _platform.WmState, 0, 2, 0, AnyPropertyType,
            &actualType, &format, &nitems, &bytesAfter, &data);
        try
        {
            if (status != 0 || data == null || format != 32 || nitems == 0) return null;
            return (int)((long*)data)[0];
        }
        finally
        {
            if (data != null) XFree(data);
        }
    }

    void ApplyNormalHints(WindowOptions options, PixelSize px, int startX, int startY)
    {
        var hints = new XSizeHints();
        long flags = 0;
        bool hasExplicitPosition = options.StartupLocation == WindowStartupLocation.Manual && options.Position.HasValue;
        bool positioned = hasExplicitPosition || options.StartupLocation != WindowStartupLocation.Manual;
        if (positioned)
        {
            flags |= hasExplicitPosition ? USPosition : PPosition;
            hints.x = startX;
            hints.y = startY;
        }

        bool fixedSize = !options.Resizable;
        bool hasMin = fixedSize || options.MinWidth.HasValue || options.MinHeight.HasValue;
        bool hasMax = fixedSize || options.MaxWidth.HasValue || options.MaxHeight.HasValue;
        int minWidth = 1, minHeight = 1;
        if (hasMin)
        {
            flags |= PMinSize;
            minWidth = fixedSize ? px.Width : X11WindowGeometry.ToPixelExtent(options.MinWidth ?? 1, RenderScale);
            minHeight = fixedSize ? px.Height : X11WindowGeometry.ToPixelExtent(options.MinHeight ?? 1, RenderScale);
            hints.min_width = minWidth;
            hints.min_height = minHeight;
        }
        if (hasMax)
        {
            flags |= PMaxSize;
            int maxWidth = fixedSize ? px.Width : options.MaxWidth is double mw ? X11WindowGeometry.ToPixelExtent(mw, RenderScale) : 32767;
            int maxHeight = fixedSize ? px.Height : options.MaxHeight is double mh ? X11WindowGeometry.ToPixelExtent(mh, RenderScale) : 32767;
            // 最小尺寸大于最大尺寸的提示会让部分窗口管理器拒绝调整：以最小尺寸为准
            if (maxWidth < minWidth) maxWidth = minWidth;
            if (maxHeight < minHeight) maxHeight = minHeight;
            hints.max_width = maxWidth;
            hints.max_height = maxHeight;
        }
        if (flags == 0) return;
        hints.flags = flags;
        XSetWMNormalHints(_display, _window, &hints);
    }

    /// <summary>映射前把状态原子写进 <c>_NET_WM_STATE</c>；没有状态时删除属性。</summary>
    void WritePreMapStates()
    {
        if (_preMapStates.Count == 0)
        {
            XDeleteProperty(_display, _window, _platform.NetWmState);
            return;
        }
        long* data = stackalloc long[_preMapStates.Count];
        int i = 0;
        foreach (var atom in _preMapStates) data[i++] = (long)atom;
        XChangeProperty(_display, _window, _platform.NetWmState, XA_ATOM, 32, PropModeReplace, (byte*)data, i);
    }

    /// <summary>把位图转为 <c>_NET_WM_ICON</c>（ARGB 像素，32 位 CARDINAL）。超过约 1M 像素时跳过，避免属性过大。</summary>
    void SetIcon(Bitmap icon)
    {
        int width = icon.Width, height = icon.Height;
        if (width <= 0 || height <= 0 || (long)width * height > 1 << 20) return;
        byte[] rgba = icon.ToRgbaUnpremultiplied();
        int pixels = width * height;
        var data = new long[pixels + 2];
        data[0] = width;
        data[1] = height;
        for (int i = 0, o = 0; i < pixels; i++, o += 4)
            data[i + 2] = ((long)rgba[o + 3] << 24) | ((long)rgba[o] << 16) | ((long)rgba[o + 1] << 8) | rgba[o + 2];
        fixed (long* p = data)
            XChangeProperty(_display, _window, _platform.NetWmIcon, XA_CARDINAL, 32, PropModeReplace, (byte*)p, data.Length);
    }

    /// <summary>X 窗口 ID（XID）。</summary>
    public ulong Handle => _window;

    /// <summary>Display* 句柄（输入法换算根窗口坐标时需要）。</summary>
    internal IntPtr Display => _display;

    /// <summary>屏幕根窗口 XID（XNSpotLocation 等根窗口坐标的参照）。</summary>
    internal ulong RootWindow => _platform.Root;

    /// <summary>客户区尺寸（设备像素）。</summary>
    public PixelSize ClientPixelSize => _clientPx;

    /// <summary>当前帧缓冲是否经 MIT-SHM 呈现（诊断用）。</summary>
    public bool UsesSharedMemory => _framebuffer.UsesSharedMemory;

    /// <summary>窗口是否使用 X 输入法上下文（否则文本输入走 XLookupString）。</summary>
    public bool HasInputContext => _ic != IntPtr.Zero;

    /// <summary>输入法桥（XIM）。</summary>
    public override TextInputMethod TextInput => _textInput ??= new X11TextInput(this);

    /// <summary>平台剪贴板（CLIPBOARD 选择）。</summary>
    public override Clipboard Clipboard => _platform.Clipboard;

    internal IntPtr InputContext => _closed ? IntPtr.Zero : _ic;

    /// <summary>输入上下文使用 on-the-spot 预编辑（XIMPreeditCallbacks）。</summary>
    internal bool PreeditCallbacks => _onTheSpot && _ic != IntPtr.Zero;

    internal void OnPreedit(int kind, IntPtr callData)
    {
        if (!_closed) ((X11TextInput)TextInput).OnPreedit(kind, callData);
    }

    internal bool WantsFrame => _frameRequested && _mapped && !_closed;

    public override void Show()
    {
        if (_closed) return;
        XMapWindow(_display, _window);
        if (_startMinimized)
        {
            // ICCCM 没有「以图标状态映射」的请求；映射后立即请求窗口管理器最小化
            _startMinimized = false;
            SendRootClientMessage(_platform.WmChangeState, IconicState, 0, 0, 0, 0);
        }
        XFlush(_display);
    }

    public override void Close()
    {
        if (_closed) return;
        Destroy();
    }

    // ------------------------------------------------------------ 自绘标题栏（design/04）

    public void BeginDragMove()
    {
        if (_closed) return;
        // EWMH：把移动交给窗口管理器；坐标用最近一次指针事件的根窗口坐标
        SendRootClientMessage(_platform.NetWmMoveresize, _lastRootX, _lastRootY,
            NetWmMoveresizeMove, 1 /* button 1 */, NetWmMoveresizeSourceApplication);
    }

    public void Minimize()
    {
        if (_closed) return;
        // ICCCM WM_CHANGE_STATE → IconicState
        SendRootClientMessage(_platform.WmChangeState, IconicState, 0, 0, 0, 0);
    }

    public void ToggleMaximize()
    {
        if (_closed) return;
        SetWindowState(_windowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
    }

    public bool IsMaximized => _windowState == WindowState.Maximized;

    /// <summary>向根窗口投递一条 EWMH / ICCCM ClientMessage（格式化按 long[5]）。</summary>
    void SendRootClientMessage(ulong type, long l0, long l1, long l2, long l3, long l4)
    {
        var ev = new XEvent();
        ev.xclient.type = ClientMessage;
        ev.xclient.window = _window;
        ev.xclient.message_type = type;
        ev.xclient.format = 32;
        ev.xclient.l0 = l0;
        ev.xclient.l1 = l1;
        ev.xclient.l2 = l2;
        ev.xclient.l3 = l3;
        ev.xclient.l4 = l4;
        XSendEvent(_display, _platform.Root, 0, SubstructureRedirectMask | SubstructureNotifyMask, &ev);
        XFlush(_display);
    }

    public override void SetTitle(string title)
    {
        if (_closed) return;
        title ??= "";
        var utf8 = Encoding.UTF8.GetBytes(title);
        var latin1 = new byte[title.Length];
        for (int i = 0; i < title.Length; i++) latin1[i] = title[i] <= 0xFF ? (byte)title[i] : (byte)'?';
        fixed (byte* u = utf8)
        fixed (byte* l = latin1)
        {
            // 现代窗口管理器读 _NET_WM_NAME（UTF-8），旧的读 WM_NAME（Latin-1 STRING）
            XChangeProperty(_display, _window, _platform.NetWmName, _platform.Utf8String, 8, PropModeReplace, u, utf8.Length);
            XChangeProperty(_display, _window, _platform.NetWmIconName, _platform.Utf8String, 8, PropModeReplace, u, utf8.Length);
            XChangeProperty(_display, _window, XA_WM_NAME, XA_STRING, 8, PropModeReplace, l, latin1.Length);
            XChangeProperty(_display, _window, XA_WM_ICON_NAME, XA_STRING, 8, PropModeReplace, l, latin1.Length);
        }
        XFlush(_display);
    }

    void SetClassHint()
    {
        // WM_CLASS = "res_name\0res_class\0"：任务栏分组与 .desktop 关联用
        string name = "awaui";
        try
        {
            var friendly = AppDomain.CurrentDomain.FriendlyName;
            if (!string.IsNullOrEmpty(friendly))
            {
                if (friendly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || friendly.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    friendly = friendly.Substring(0, friendly.Length - 4);
                if (friendly.Length > 0) name = friendly;
            }
        }
        catch { }
        string cls = char.ToUpperInvariant(name[0]) + name.Substring(1);
        var data = new byte[name.Length + cls.Length + 2];
        int n = 0;
        foreach (var c in name) data[n++] = c <= 0xFF ? (byte)c : (byte)'?';
        data[n++] = 0;
        foreach (var c in cls) data[n++] = c <= 0xFF ? (byte)c : (byte)'?';
        data[n] = 0;
        fixed (byte* p = data) XChangeProperty(_display, _window, XA_WM_CLASS, XA_STRING, 8, PropModeReplace, p, data.Length);
    }

    public override void SetCursor(CursorShape cursor)
    {
        if (_closed) return;
        XDefineCursor(_display, _window, _platform.GetCursor(cursor));
        XFlush(_display);
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
    }

    // ---------------------------------------------------------------- 帧与呈现

    internal void DeliverFrame(TimeSpan timestamp)
    {
        _frameRequested = false; // 先清除：OnFrame 内的请求留给下一个节拍
        _inFrame = true;
        try { Events?.OnFrame(timestamp); }
        finally { _inFrame = false; }
        if (_closed) return;
        if (_presentPending)
        {
            _presentPending = false;
            PresentAll();
        }
    }

    void PresentAll()
    {
        if (_closed) return;
        var size = _framebuffer.Size;
        _framebuffer.Present(0, 0, size.Width, size.Height);
    }

    /// <summary>渲染器释放了帧缓冲锁。</summary>
    void OnFramebufferCommitted()
    {
        if (_closed) return;
        _framebuffer.Commit();
        if (_inFrame) { _presentPending = true; return; } // 帧末统一呈现
        if (_platform.Dispatcher.CheckAccess()) PresentAll();
        else _platform.Dispatcher.Post(PresentAll);
    }

    internal void OnShmCompletion() => _framebuffer.OnShmCompletion();

    // ---------------------------------------------------------------- 事件

    internal void HandleEvent(XEvent* ev)
    {
        if (_closed) return;
        switch (ev->type)
        {
            case Expose: OnExpose(&ev->xexpose); break;
            case ConfigureNotify: OnConfigure(&ev->xconfigure); break;
            case MapNotify:
                _mapped = true;
                RefreshStateFromProperties();
                break;
            case UnmapNotify:
                _mapped = false;
                RefreshStateFromProperties();
                break;
            case PropertyNotify:
                if (ev->xproperty.window == _window &&
                    (ev->xproperty.atom == _platform.NetWmState || ev->xproperty.atom == _platform.WmState))
                    RefreshStateFromProperties();
                break;
            case ClientMessage:
                if (ev->xclient.message_type == _platform.WmProtocols && (ulong)ev->xclient.l0 == _platform.WmDeleteWindow)
                {
                    if (Events?.OnCloseRequested() ?? true) Destroy();
                }
                break;
            case FocusIn:
            case FocusOut:
                OnFocus(ev->type == FocusIn, &ev->xfocus);
                break;
            case KeyPress:
            case KeyRelease:
                OnKey(ev);
                break;
            case ButtonPress:
            case ButtonRelease:
                OnButton(ev->type == ButtonPress, &ev->xbutton);
                break;
            case MotionNotify:
                OnMotion(&ev->xmotion);
                break;
            case EnterNotify:
            case LeaveNotify:
                OnCrossing(ev->type == EnterNotify, &ev->xcrossing);
                break;
        }
    }

    void OnExpose(XExposeEvent* e)
    {
        _exposeX0 = Math.Min(_exposeX0, e->x);
        _exposeY0 = Math.Min(_exposeY0, e->y);
        _exposeX1 = Math.Max(_exposeX1, e->x + e->width);
        _exposeY1 = Math.Max(_exposeY1, e->y + e->height);
        if (e->count > 0) return; // 同一批 Expose 的最后一个才呈现
        int x = _exposeX0, y = _exposeY0, w = _exposeX1 - _exposeX0, h = _exposeY1 - _exposeY0;
        _exposeX0 = _exposeY0 = int.MaxValue;
        _exposeX1 = _exposeY1 = int.MinValue;
        if (_framebuffer.HasContent) _framebuffer.Present(x, y, w, h);
        else _frameRequested = true; // 还没有内容（首次映射）：出一帧
    }

    void OnConfigure(XConfigureEvent* e)
    {
        UpdatePosition(e);
        var size = new PixelSize(Math.Max(1, e->width), Math.Max(1, e->height));
        if (size == _clientPx) return; // 只是移动
        _clientPx = size;
        Events?.OnResized(ClientSize, RenderScale);
        _frameRequested = true;
    }

    /// <summary>
    /// ConfigureNotify 的 x/y 是相对父窗口（窗口管理器重设父窗口后是框架）的坐标；用 XTranslateCoordinates
    /// 换成根窗口坐标，再按缩放换成逻辑像素。换算失败时退回事件里的坐标。
    /// </summary>
    void UpdatePosition(XConfigureEvent* e)
    {
        int rootX = e->x, rootY = e->y;
        ulong child;
        if (XTranslateCoordinates(_display, _window, _platform.Root, 0, 0, &rootX, &rootY, &child) == 0)
        {
            rootX = e->x;
            rootY = e->y;
        }
        double scale = RenderScale;
        if (!(scale > 0) || double.IsInfinity(scale)) scale = 1;
        var position = new Point((float)(rootX / scale), (float)(rootY / scale));
        if (position == _position) return;
        _position = position;
        (Events as IWindowStateEvents)?.OnPositionChanged(position);
    }

    void OnFocus(bool focused, XFocusChangeEvent* e)
    {
        // NotifyPointer 是指针所在窗口的「伴随」焦点事件，不代表本窗口获得键盘焦点
        if (e->detail == NotifyPointer) return;
        if (_ic != IntPtr.Zero)
        {
            // 没有文本客户端或客户端是密码框时不让输入上下文获得焦点：窗口重新获得焦点时也要保持这一状态，
            // 否则密码框会重新启用输入法组合（XIC 与窗口的关联本身保留，见 X11TextInput.OnClientChanged）。
            if (focused && _textInput?.Client is { Options.IsPassword: false }) XSetICFocus(_ic);
            else XUnsetICFocus(_ic);
        }
        if (!focused) Array.Clear(_keyDown, 0, _keyDown.Length); // 失焦期间的抬起收不到
        if (focused == _focused) return;
        _focused = focused;
        Events?.OnFocusChanged(focused);
        (Events as IWindowLifecycleEvents)?.OnActivated(focused);
    }

    void OnKey(XEvent* ev)
    {
        var e = &ev->xkey;
        bool down = e->type == KeyPress;
        uint code = e->keycode;
        var events = Events;

        if (!down && !_platform.DetectableAutoRepeat && IsAutoRepeatRelease(e)) return;

        // 按下时只调用一次查找（Xutf8LookupString 推进输入法状态），同时得到 keysym 与文本；抬起时用 XLookupString 取 keysym
        ulong keysym = 0;
        string? text = null;
        if (down) text = LookupText(e, out keysym);
        else keysym = LookupKeysym(e);

        // 输入法提交文本时会合成 keycode 为 0 的 KeyPress：只有文本，没有物理键
        if (code != 0 && code < 256)
        {
            bool repeat = down && _keyDown[code];
            _keyDown[code] = down;
            var key = KeyMap.Get(code);
            var mods = KeyMap.Modifiers(e->state, key, down);
            string logical = X11Keysyms.ToKeyValue(keysym, text) ?? KeyValues.FromPhysicalKey(key, mods);
            events?.OnKey(new RawKeyEvent(down ? RawKeyEventType.Down : RawKeyEventType.Up, key, mods, repeat, (int)code,
                _platform.ServerTime(e->time), logical));
            if (_closed) return;
        }
        if (!down || events == null) return;

        // Ctrl / Alt 组合是快捷键，不产生文本（AltGr 是 Mod5，不受影响）
        if ((e->state & (ControlMask | Mod1Mask)) != 0) return;
        if (!string.IsNullOrEmpty(text) && IsPrintable(text!)) DeliverText(text!);
    }

    /// <summary>已提交的文本：有输入法客户端时交给它，否则经 OnTextInput。</summary>
    void DeliverText(string text)
    {
        if (_textInput?.Client is { } client) client.Commit(text);
        else Events?.OnTextInput(text);
    }

    ulong LookupKeysym(XKeyEvent* e)
    {
        ulong keysym;
        var buffer = stackalloc byte[16];
        XLookupString(e, buffer, 16, &keysym, null);
        return keysym;
    }

    /// <summary>不支持可检测自动重复时，自动重复表现为同一时间戳的 KeyRelease + KeyPress，丢弃这个 KeyRelease。</summary>
    bool IsAutoRepeatRelease(XKeyEvent* e)
    {
        if (XEventsQueued(_display, QueuedAfterReading) == 0) return false;
        XEvent next;
        XPeekEvent(_display, &next);
        return next.type == KeyPress && next.xkey.window == e->window && next.xkey.keycode == e->keycode && next.xkey.time == e->time;
    }

    string? LookupText(XKeyEvent* e, out ulong keysym)
    {
        ulong sym = 0;
        var buffer = stackalloc byte[64];
        string? result = null;
        if (_ic != IntPtr.Zero)
        {
            int status;
            int len = Xutf8LookupString(_ic, e, buffer, 64, &sym, &status);
            if (status == XBufferOverflow && len > 0)
            {
                // 输入法一次提交的长文本：按需要的长度再取一次（上限 1 MB）
                int size = Math.Min(len, 1 << 20) + 1;
                var big = new byte[size];
                fixed (byte* p = big)
                {
                    len = Xutf8LookupString(_ic, e, p, big.Length, &sym, &status);
                    result = status is XLookupChars or XLookupBoth && len > 0 ? Encoding.UTF8.GetString(p, Math.Min(len, big.Length)) : null;
                }
            }
            else result = status is XLookupChars or XLookupBoth && len > 0 ? Encoding.UTF8.GetString(buffer, len) : null;
            keysym = status is XLookupKeySym or XLookupBoth ? sym : 0;
            return result;
        }
        // 没有输入法：XLookupString 返回 Latin-1
        int n = XLookupString(e, buffer, 64, &sym, null);
        keysym = sym;
        if (n <= 0) return null;
        var sb = new StringBuilder(n);
        for (int i = 0; i < n; i++) sb.Append((char)buffer[i]);
        return sb.ToString();
    }

    static bool IsPrintable(string text)
    {
        foreach (var c in text)
            if (c < 0x20 || c == 0x7F || (c >= 0x80 && c < 0xA0)) return false; // C0 / DEL / C1 控制字符
        return true;
    }

    Point ToLogical(int x, int y)
    {
        double s = RenderScale;
        return new Point((float)(x / s), (float)(y / s));
    }

    void OnButton(bool down, XButtonEvent* e)
    {
        var events = Events;
        var pos = ToLogical(e->x, e->y);
        var time = _platform.ServerTime(e->time);
        var mods = KeyMap.Modifiers(e->state);
        _lastRootX = e->x_root;
        _lastRootY = e->y_root;

        // 滚轮是按钮 4–7（只有按下有意义）。符号约定与 macOS/Win32 后端一致：dy > 0 向上，dx > 0 向左（换算见 Input/X11Wheel.cs）
        if (X11Wheel.IsWheelButton(e->button))
        {
            if (!down) return;
            X11Wheel.TryGetDelta(e->button, WheelDeltas.DefaultLinesPerNotch, out float dx, out float dy);
            events?.OnPointer(RawPointerEvent.CreateWheel(pos, dx, dy, WheelDeltaMode.Line, precise: false, ScrollPhase.None, ScrollPhase.None,
                _buttons, mods, time));
            return;
        }

        var changed = e->button switch
        {
            1 => PointerButtons.Left,
            2 => PointerButtons.Middle,
            3 => PointerButtons.Right,
            8 => PointerButtons.X1,
            9 => PointerButtons.X2,
            _ => PointerButtons.None,
        };
        if (changed == PointerButtons.None) return;
        if (down) _buttons |= changed;
        else _buttons &= ~changed;
        events?.OnPointer(new RawPointerEvent(down ? RawPointerEventType.Down : RawPointerEventType.Up, PointerType.Mouse, 0, pos,
            changed, _buttons, mods, time, 0.5f));
        if (_closed) return;

        // 拖出窗口后在外面抬起：补发推迟的离开
        if (!down && _buttons == PointerButtons.None && _leavePending)
        {
            _leavePending = false;
            _inside = false;
            Events?.OnPointer(new RawPointerEvent(RawPointerEventType.Leave, PointerType.Mouse, 0, pos, PointerButtons.None,
                PointerButtons.None, mods, time, 0f));
        }
    }

    void OnMotion(XMotionEvent* e)
    {
        _lastRootX = e->x_root;
        _lastRootY = e->y_root;
        float pressure = _buttons == PointerButtons.None ? 0f : 0.5f;
        Events?.OnPointer(new RawPointerEvent(RawPointerEventType.Move, PointerType.Mouse, 0, ToLogical(e->x, e->y),
            PointerButtons.None, _buttons, KeyMap.Modifiers(e->state), _platform.ServerTime(e->time), pressure));
    }

    void OnCrossing(bool enter, XCrossingEvent* e)
    {
        // 没有子窗口，NotifyInferior 不会发生在我们身上；Grab/Ungrab 模式（窗口管理器抓取）按普通进出处理
        var pos = ToLogical(e->x, e->y);
        var time = _platform.ServerTime(e->time);
        var mods = KeyMap.Modifiers(e->state);
        _lastRootX = e->x_root;
        _lastRootY = e->y_root;
        if (enter)
        {
            if (_leavePending) { _leavePending = false; return; } // 拖出又拖回：对上层而言从未离开
            if (_inside) return;
            _inside = true;
            Events?.OnPointer(new RawPointerEvent(RawPointerEventType.Enter, PointerType.Mouse, 0, pos, PointerButtons.None,
                _buttons, mods, time, 0f));
        }
        else
        {
            if (!_inside || _leavePending) return;
            if (_buttons != PointerButtons.None && e->mode == NotifyNormal)
            {
                _leavePending = true; // 隐式抓取期间继续收到移动与抬起
                return;
            }
            _inside = false;
            Events?.OnPointer(new RawPointerEvent(RawPointerEventType.Leave, PointerType.Mouse, 0, pos, PointerButtons.None,
                _buttons, mods, time, 0f));
        }
    }

    // ---------------------------------------------------------------- 关闭

    void Destroy()
    {
        if (_closed) return;
        _closed = true;
        Events?.OnClosed();
        if (_ic != IntPtr.Zero)
        {
            XDestroyIC(_ic);
            _ic = IntPtr.Zero;
        }
        if (_icCallbacks != IntPtr.Zero)
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(_icCallbacks);
            _icCallbacks = IntPtr.Zero;
        }
        _framebuffer.Dispose();
        XFreeGC(_display, _gc);
        XDestroyWindow(_display, _window);
        XFlush(_display);
        _platform.OnWindowClosed(this);
    }

    /// <summary>软件呈现用的帧缓冲表面。</summary>
    sealed class X11Surface : NativeSurface, IFramebufferSurface
    {
        readonly X11Window _owner;

        public X11Surface(X11Window owner) => _owner = owner;

        public override PixelSize Size => _owner._clientPx;
        public override double Scale => _owner.RenderScale;

        public ILockedFramebuffer Lock()
        {
            if (_owner._closed) throw new ObjectDisposedException(nameof(X11Window));
            var fb = _owner._framebuffer;
            fb.Prepare(_owner._clientPx);
            return new Locked(_owner, fb.Address, fb.Size, fb.RowBytes, fb.Format);
        }

        sealed class Locked : ILockedFramebuffer
        {
            readonly X11Window _owner;
            bool _disposed;

            public Locked(X11Window owner, IntPtr address, PixelSize size, int rowBytes, PixelFormat format)
            {
                _owner = owner;
                Address = address;
                Size = size;
                RowBytes = rowBytes;
                Format = format;
            }

            public IntPtr Address { get; }
            public PixelSize Size { get; }
            public int RowBytes { get; }
            public PixelFormat Format { get; }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _owner.OnFramebufferCommitted();
            }
        }
    }
}
