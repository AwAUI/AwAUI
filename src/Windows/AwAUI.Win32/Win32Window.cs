using System;
using System.Runtime.InteropServices;
using AwAUI.Graphics;
using AwAUI.Platform;
using AwAUI.Win32.Interop;
using static AwAUI.Win32.Interop.Const;

namespace AwAUI.Win32;

/// <summary>
/// HWND 顶层窗口。M0 采用软件呈现：框架渲染到 DIB section（BGRA、自上而下），WM_PAINT 中 BitBlt 到窗口。
/// GPU 呈现（D3D11 交换链）在 M2 加入，届时 <see cref="Surface"/> 额外提供对应能力。
/// 坐标约定：<see cref="ClientSize"/> 与指针位置为逻辑像素 = 物理像素 / (DPI / 96)。
/// </summary>
public sealed unsafe class Win32Window : WindowImpl, IWindowChrome
{
    /// <summary>Windows 桌面使用常驻的经典滚动条。</summary>
    public override ScrollbarStyle PreferredScrollbarStyle => ScrollbarStyle.Classic;

    readonly Win32Platform _platform;
    readonly Win32Surface _surface;
    readonly uint _style;
    readonly uint _exStyle;
    readonly WindowState _initialState;
    readonly bool _topmost;
    readonly double? _minWidth;
    readonly double? _minHeight;
    readonly double? _maxWidth;
    readonly double? _maxHeight;

    IntPtr _hwnd;
    uint _dpi = USER_DEFAULT_SCREEN_DPI;
    PixelSize _clientPx;
    Size _lastSize;
    double _lastScale;
    bool _inPaint;
    bool _framePending;
    bool _closed;
    bool _shown;
    bool _fullScreen;
    uint _savedStyle;
    RECT _savedRect;
    IntPtr _windowIcon;
    WindowState _lastReportedState = WindowState.Normal;
    Point _lastWindowPosition;
    bool _hasWindowPosition;
    bool _hasReportedActivation;
    bool _lastActivation;
    bool _tracking;
    PointerButtons _buttons;
    Point _lastPosition;
    Utf16Assembler _chars;
    /// <summary>刚由输入法提交结果串：随后投递的 WM_CHAR 是同一结果，丢弃（下一次 WM_KEYUP 清除）。</summary>
    bool _ignoreWmChar;
    Win32TextInput? _textInput;
    IntPtr _cursor;
    Win32AccessibilityBridge? _accessibility;

    internal Win32Window(Win32Platform platform, WindowOptions options)
    {
        _platform = platform;
        _surface = new Win32Surface(this);
        _initialState = options.State;
        _topmost = options.Topmost;
        _minWidth = options.MinWidth;
        _minHeight = options.MinHeight;
        _maxWidth = options.MaxWidth;
        _maxHeight = options.MaxHeight;
        // 无边框：WS_POPUP 没有非客户区，客户区铺满整个窗口；WS_EX_APPWINDOW 让它在任务栏出现。
        bool borderless = options.Decorations == WindowDecorations.None;
        _style = borderless
            ? WS_POPUP
            : options.Resizable ? WS_OVERLAPPEDWINDOW : WS_OVERLAPPEDWINDOW & ~(WS_THICKFRAME | WS_MAXIMIZEBOX);
        uint exStyle = borderless ? WS_EX_APPWINDOW : 0;
        if (!options.ShowInTaskbar)
        {
            // 工具窗口不出现在任务栏；去掉 WS_EX_APPWINDOW 以免两者抵消。
            exStyle &= ~WS_EX_APPWINDOW;
            exStyle |= WS_EX_TOOLWINDOW;
        }
        if (options.Topmost) exStyle |= WS_EX_TOPMOST;
        _exStyle = exStyle;
        _cursor = LoadCursor(CursorShape.Arrow);

        IntPtr hwnd;
        platform.BeginCreate(this);
        try
        {
            fixed (char* title = options.Title ?? "")
                hwnd = User32.CreateWindowExW(_exStyle, (char*)platform.WindowClass, title, _style,
                    CW_USEDEFAULT, CW_USEDEFAULT, CW_USEDEFAULT, CW_USEDEFAULT, IntPtr.Zero, IntPtr.Zero, platform.Instance, IntPtr.Zero);
        }
        finally
        {
            platform.EndCreate();
        }
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowExW failed, error {Marshal.GetLastWin32Error()}.");
        _hwnd = hwnd;

        // 窗口创建后才能知道它所在显示器的 DPI：据此把逻辑客户区尺寸换算为窗口尺寸，并按要求放置
        _dpi = Win32Platform.GetWindowDpi(hwnd);
        SetInitialBounds(options);
        UpdateClientSize();
        _lastSize = ClientSize;
        _lastScale = RenderScale;
        _lastWindowPosition = Position;
        _hasWindowPosition = true;

        if (_topmost) SetTopmost(true);
        if (options.Icon is { } icon) ApplyIcon(icon);
    }

    public override NativeSurface Surface => _surface;

    public override Size ClientSize
    {
        get
        {
            double scale = RenderScale;
            return new Size((float)(_clientPx.Width / scale), (float)(_clientPx.Height / scale));
        }
    }

    public override double RenderScale => _dpi / (double)USER_DEFAULT_SCREEN_DPI;

    // ---------------------------------------------------------------- 几何、状态与激活（design/04）

    /// <summary>窗口左上角的屏幕逻辑坐标（含边框）。</summary>
    public override Point Position
    {
        get
        {
            if (_closed || _hwnd == IntPtr.Zero) return default;
            // 最小化时 GetWindowRect 返回 (-32000, -32000)：改为报告进入最小化前的最后一个位置。
            if (_hasWindowPosition && User32.IsIconic(_hwnd) != 0) return _lastWindowPosition;
            RECT r;
            if (User32.GetWindowRect(_hwnd, &r) == 0) return default;
            double scale = RenderScale;
            return new Point((float)(r.Left / scale), (float)(r.Top / scale));
        }
    }

    public override void SetPosition(Point position)
    {
        if (_closed || _hwnd == IntPtr.Zero) return;
        if (!_platform.Dispatcher.CheckAccess()) { _platform.Dispatcher.Post(() => SetPosition(position)); return; }
        double scale = RenderScale;
        User32.SetWindowPos(_hwnd, IntPtr.Zero, ToPhysical(position.X, scale), ToPhysical(position.Y, scale), 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public override AwAUI.Platform.WindowState WindowState
    {
        get
        {
            if (_closed || _hwnd == IntPtr.Zero) return AwAUI.Platform.WindowState.Normal;
            if (_fullScreen) return AwAUI.Platform.WindowState.FullScreen;
            if (User32.IsIconic(_hwnd) != 0) return AwAUI.Platform.WindowState.Minimized;
            if (User32.IsZoomed(_hwnd) != 0) return AwAUI.Platform.WindowState.Maximized;
            return AwAUI.Platform.WindowState.Normal;
        }
    }

    public override void SetWindowState(AwAUI.Platform.WindowState state)
    {
        if (_closed || _hwnd == IntPtr.Zero) return;
        if (!_platform.Dispatcher.CheckAccess()) { _platform.Dispatcher.Post(() => SetWindowState(state)); return; }
        if (state != AwAUI.Platform.WindowState.FullScreen && _fullScreen) ExitFullScreen();
        switch (state)
        {
            case AwAUI.Platform.WindowState.FullScreen:
                EnterFullScreen();
                break;
            case AwAUI.Platform.WindowState.Minimized:
                User32.ShowWindow(_hwnd, SW_MINIMIZE);
                break;
            case AwAUI.Platform.WindowState.Maximized:
                User32.ShowWindow(_hwnd, SW_MAXIMIZE);
                break;
            default:
                User32.ShowWindow(_hwnd, SW_RESTORE);
                break;
        }
        RaiseStateChangedIfNeeded();
        if (WindowState != AwAUI.Platform.WindowState.Minimized) RaisePositionChangedIfNeeded();
    }

    public override bool Topmost
    {
        get
        {
            if (_closed || _hwnd == IntPtr.Zero) return false;
            uint ex = (uint)(ulong)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
            return (ex & WS_EX_TOPMOST) != 0;
        }
        set
        {
            if (_closed || _hwnd == IntPtr.Zero) return;
            if (!_platform.Dispatcher.CheckAccess()) { _platform.Dispatcher.Post(() => Topmost = value); return; }
            SetTopmost(value);
        }
    }

    public override bool IsActive => !_closed && _hwnd != IntPtr.Zero && User32.GetForegroundWindow() == _hwnd;

    public override void Activate()
    {
        if (_closed || _hwnd == IntPtr.Zero) return;
        if (!_platform.Dispatcher.CheckAccess()) { _platform.Dispatcher.Post(Activate); return; }
        if (User32.IsIconic(_hwnd) != 0) User32.ShowWindow(_hwnd, SW_RESTORE);
        User32.SetForegroundWindow(_hwnd);
    }

    public override Thickness SafeAreaInsets => Thickness.Zero;

    public override Screens Screens => _platform.Screens;
    public override StorageProvider StorageProvider => _platform.StorageProvider;
    public override Launcher Launcher => _platform.Launcher;

    public override void Invalidate(in Rect rect)
    {
        if (_closed || _hwnd == IntPtr.Zero) return;
        var copy = rect; // in 参数不能被 lambda 捕获
        if (!_platform.Dispatcher.CheckAccess()) { _platform.Dispatcher.Post(() => Invalidate(copy)); return; }
        if (copy.IsEmpty || !copy.IsFinite)
        {
            User32.InvalidateRect(_hwnd, null, 0);
            return;
        }
        double scale = RenderScale;
        var rc = new RECT(
            ToPhysical(copy.Left, scale), ToPhysical(copy.Top, scale),
            ToPhysical(copy.Right, scale), ToPhysical(copy.Bottom, scale));
        User32.InvalidateRect(_hwnd, &rc, 0);
    }

    static int ToPhysical(double logical, double scale)
    {
        double value = Math.Round(logical * scale);
        if (double.IsNaN(value)) return 0;
        if (value > int.MaxValue / 2.0) return int.MaxValue / 2;
        if (value < int.MinValue / 2.0) return int.MinValue / 2;
        return (int)value;
    }

    void SetTopmost(bool topmost)
    {
        if (_closed || _hwnd == IntPtr.Zero) return;
        User32.SetWindowPos(_hwnd, topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
        IntPtr.Size == 8 ? User32.GetWindowLongPtrW(hwnd, index) : new IntPtr(User32.GetWindowLongW(hwnd, index));

    static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value) =>
        IntPtr.Size == 8 ? User32.SetWindowLongPtrW(hwnd, index, value) : new IntPtr(User32.SetWindowLongW(hwnd, index, value.ToInt32()));

    void EnterFullScreen()
    {
        if (_fullScreen || _closed) return;
        // 先还原：保存的是普通状态的矩形，退出全屏时回到这里，再由调用方的目标状态决定是否最大化。
        if (User32.IsIconic(_hwnd) != 0 || User32.IsZoomed(_hwnd) != 0) User32.ShowWindow(_hwnd, SW_RESTORE);
        RECT saved;
        if (User32.GetWindowRect(_hwnd, &saved) != 0) _savedRect = saved;
        _savedStyle = (uint)(ulong)GetWindowLongPtr(_hwnd, GWL_STYLE);
        uint style = (_savedStyle & ~(WS_OVERLAPPEDWINDOW)) | WS_POPUP;
        SetWindowLongPtr(_hwnd, GWL_STYLE, new IntPtr(style));

        var monitor = User32.MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        RECT bounds = _savedRect;
        if (monitor != IntPtr.Zero && User32.GetMonitorInfoW(monitor, &info) != 0) bounds = info.rcMonitor;

        _fullScreen = true;
        User32.SetWindowPos(_hwnd, HWND_TOP, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            SWP_FRAMECHANGED | SWP_NOACTIVATE);
        UpdateClientSize();
        CheckResize();
        RaiseStateChangedIfNeeded();
    }

    void ExitFullScreen()
    {
        if (!_fullScreen || _closed) return;
        _fullScreen = false;
        SetWindowLongPtr(_hwnd, GWL_STYLE, new IntPtr(_savedStyle));
        User32.SetWindowPos(_hwnd, IntPtr.Zero, _savedRect.Left, _savedRect.Top, _savedRect.Width, _savedRect.Height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        UpdateClientSize();
        CheckResize();
        RaiseStateChangedIfNeeded();
    }

    // ---------------------------------------------------------------- 窗口图标

    /// <summary>把窗口图标位图转成 HICON 并设为 WM_SETICON 的大/小图标（窗口类已注册默认图标）。</summary>
    void ApplyIcon(Bitmap bitmap)
    {
        var icon = CreateIcon(bitmap);
        if (icon == IntPtr.Zero) return;
        _windowIcon = icon;
        User32.SendMessageW(_hwnd, WM_SETICON, new IntPtr(ICON_BIG), icon);
        User32.SendMessageW(_hwnd, WM_SETICON, new IntPtr(ICON_SMALL), icon);
    }

    /// <summary>
    /// Bitmap（预乘 RGBA）→ HICON：32 位自下而上的颜色 DIB（直通 BGRA）+ 1 位 AND 掩码（alpha == 0 处置位）。
    /// 失败时返回 <see cref="IntPtr.Zero"/>，由窗口保留默认图标。
    /// </summary>
    static unsafe IntPtr CreateIcon(Bitmap bitmap)
    {
        int width = bitmap.Width, height = bitmap.Height;
        long pixels = (long)width * height;
        if (width <= 0 || height <= 0 || pixels > Win32Dib.MaxPixels) return IntPtr.Zero;

        byte[] rgba;
        try { rgba = bitmap.ToRgbaUnpremultiplied(); }
        catch (Exception e) when (e is not OutOfMemoryException) { return IntPtr.Zero; }

        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = width,
                biHeight = height, // 正数：自下而上，CreateDIBSection 的内存首行是最底行
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
                biSizeImage = (uint)(width * height * 4),
            },
        };
        void* colorBits;
        var color = Gdi32.CreateDIBSection(IntPtr.Zero, &info, DIB_RGB_COLORS, &colorBits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero || colorBits == null) return IntPtr.Zero;

        int maskStride = ((width + 15) / 16) * 2;
        var maskBits = new byte[maskStride * height];
        byte* dst = (byte*)colorBits;
        for (int row = 0; row < height; row++)
        {
            int srcRow = (height - 1 - row) * width * 4;
            int maskRow = row * maskStride;
            for (int x = 0; x < width; x++)
            {
                int s = srcRow + x * 4;
                *dst++ = rgba[s + 2]; // B
                *dst++ = rgba[s + 1]; // G
                *dst++ = rgba[s];     // R
                *dst++ = rgba[s + 3]; // A
                if (rgba[s + 3] == 0) maskBits[maskRow + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            }
        }

        // 单色位图：1 位 = 透明（AND 掩码）。初值为 0（不透明），再写入透明位。
        var mask = Gdi32.CreateBitmap(width, height, 1, 1, null);
        if (mask == IntPtr.Zero)
        {
            Gdi32.DeleteObject(color);
            return IntPtr.Zero;
        }
        fixed (byte* mb = maskBits)
            Gdi32.SetBitmapBits(mask, (uint)maskBits.Length, mb);

        var iconInfo = new ICONINFO { fIcon = 1, hbmMask = mask, hbmColor = color };
        var icon = User32.CreateIconIndirect(&iconInfo);
        Gdi32.DeleteObject(mask);
        Gdi32.DeleteObject(color);
        return icon;
    }

    public override T? GetFeature<T>() where T : class =>
        base.GetFeature<T>() ?? _platform.AppearancePreference as T ?? _platform.ReducedMotionPreference as T;

    /// <summary>HWND 句柄（窗口销毁后为 <see cref="IntPtr.Zero"/>）。</summary>
    public IntPtr Handle => _hwnd;

    /// <summary>输入法桥（IMM32）。</summary>
    public override TextInputMethod TextInput => _textInput ??= new Win32TextInput(this);

    /// <summary>
    /// UI Automation bridge（design/10）。惰性创建；环境变量 <c>AWAUI_WIN32_UIA=0</c> 或 UIA 不可用时返回 null。
    /// </summary>
    public override AccessibilityBridge? Accessibility
    {
        get
        {
            if (_accessibility is not null) return _accessibility;
            if (_closed || _hwnd == IntPtr.Zero) return null;
            _accessibility = _platform.CreateAccessibilityBridge(this);
            if (_accessibility is not null) _accessibility.RefreshGeometry(_hwnd, RenderScale);
            return _accessibility;
        }
    }

    /// <summary>平台剪贴板（CF_UNICODETEXT）。</summary>
    public override Clipboard Clipboard => _platform.Clipboard;

    Win32TextInput Ime => (Win32TextInput)TextInput;

    /// <summary>客户区的设备像素尺寸（帧缓冲尺寸以此为准，避免逻辑尺寸往返换算的舍入误差）。</summary>
    internal PixelSize ClientPixelSize => _clientPx;

    public override void Show()
    {
        if (_closed) return;
        if (!_platform.Dispatcher.CheckAccess()) { _platform.Dispatcher.Post(Show); return; }
        if (_shown)
        {
            // 再次 Show 只负责把最小化的窗口带回前台。
            if (User32.IsIconic(_hwnd) != 0) User32.ShowWindow(_hwnd, SW_RESTORE);
            return;
        }
        _shown = true;

        if (_initialState == AwAUI.Platform.WindowState.FullScreen)
        {
            User32.ShowWindow(_hwnd, SW_SHOWNORMAL);
            EnterFullScreen();
        }
        else
        {
            int command = _initialState switch
            {
                AwAUI.Platform.WindowState.Minimized => SW_SHOWMINIMIZED,
                AwAUI.Platform.WindowState.Maximized => SW_SHOWMAXIMIZED,
                _ => SW_SHOWNORMAL,
            };
            User32.ShowWindow(_hwnd, command);
        }

        // 桌面没有安全区（刘海、软键盘占位）；向框架报告一次零内边距。
        (Events as IWindowLifecycleEvents)?.OnSafeAreaChanged(Thickness.Zero);
        RaiseStateChangedIfNeeded();
        if (WindowState != AwAUI.Platform.WindowState.Minimized) RaisePositionChangedIfNeeded();
    }

    public override void Close()
    {
        if (_closed || _hwnd == IntPtr.Zero) return;
        // DestroyWindow 只能在所属线程调用；绘制回调中关闭会销毁正在绘制的窗口，推迟到当前消息之后
        if (_inPaint || !_platform.Dispatcher.CheckAccess())
        {
            _platform.Dispatcher.Post(Close);
            return;
        }
        User32.DestroyWindow(_hwnd); // 同步触发 WM_DESTROY / WM_NCDESTROY
    }

    public override void SetTitle(string title)
    {
        if (_closed) return;
        fixed (char* p = title ?? "")
            User32.SetWindowTextW(_hwnd, p);
    }

    // ------------------------------------------------------------ 自绘标题栏（design/04）

    public void BeginDragMove()
    {
        if (_closed || _hwnd == IntPtr.Zero) return;
        // 交还给 DefWindowProc 的移动循环；拖动结束后 WM_CAPTURECHANGED 会让框架清除按下状态
        User32.ReleaseCapture();
        User32.SendMessageW(_hwnd, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
    }

    public void Minimize()
    {
        if (_closed) return;
        if (_fullScreen) ExitFullScreen();
        User32.ShowWindow(_hwnd, SW_MINIMIZE);
    }

    public void ToggleMaximize()
    {
        if (_closed) return;
        if (_fullScreen) ExitFullScreen();
        User32.ShowWindow(_hwnd, User32.IsZoomed(_hwnd) != 0 ? SW_RESTORE : SW_MAXIMIZE);
    }

    public bool IsMaximized => !_closed && User32.IsZoomed(_hwnd) != 0;

    public override void RequestFrame()
    {
        if (_closed) return;
        if (!_platform.Dispatcher.CheckAccess()) { _platform.Dispatcher.Post(RequestFrame); return; }
        // 绘制中直接 InvalidateRect 会被随后的 EndPaint 验证掉，推迟到本次绘制结束后
        if (_inPaint) { _framePending = true; return; }
        User32.InvalidateRect(_hwnd, null, 0);
    }

    public override void SetCursor(CursorShape cursor)
    {
        if (_closed) return;
        if (!_platform.Dispatcher.CheckAccess()) { _platform.Dispatcher.Post(() => SetCursor(cursor)); return; }
        _cursor = LoadCursor(cursor);
        // 指针在客户区内时立即生效；否则等下一次 WM_SETCURSOR
        if (_tracking) User32.SetCursor(_cursor);
    }

    static IntPtr LoadCursor(CursorShape shape)
    {
        int id = shape switch
        {
            CursorShape.IBeam => IDC_IBEAM,
            CursorShape.Hand => IDC_HAND,
            CursorShape.Crosshair => IDC_CROSS,
            CursorShape.ResizeHorizontal => IDC_SIZEWE,
            CursorShape.ResizeVertical => IDC_SIZENS,
            CursorShape.Wait => IDC_WAIT,
            CursorShape.NotAllowed => IDC_NO,
            CursorShape.None => 0,
            _ => IDC_ARROW,
        };
        // 系统共享光标无需销毁；None 用空光标（隐藏）
        return id == 0 ? IntPtr.Zero : User32.LoadCursorW(IntPtr.Zero, new IntPtr(id));
    }

    // ---------------------------------------------------------------- 尺寸与 DPI

    void SetInitialBounds(WindowOptions options)
    {
        // 目标显示器决定缩放：显式位置按其所在显示器，居中用主显示器，未给信息时用窗口当前 DPI。
        double scale = RenderScale;
        Point? requested = options.Position;
        Screen? targetScreen = null;
        if (requested is Point position)
        {
            try { targetScreen = _platform.Screens.FromPoint(position); }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
            if (targetScreen is not null) scale = targetScreen.Scaling;
        }
        else if (options.StartupLocation is WindowStartupLocation.CenterScreen or WindowStartupLocation.CenterOwner)
        {
            try { targetScreen = _platform.Screens.Primary; scale = targetScreen.Scaling; }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        }

        var r = new RECT(0, 0, ToPhysical(options.Size.Width, scale), ToPhysical(options.Size.Height, scale));
        Win32Platform.AdjustWindowRect(&r, _style, _exStyle, (uint)Math.Max(96, Math.Round(scale * USER_DEFAULT_SCREEN_DPI)));
        int width = r.Width, height = r.Height;

        uint flags = SWP_NOZORDER | SWP_NOACTIVATE;
        int x = 0, y = 0;
        if (requested is Point p)
        {
            x = ToPhysical(p.X, scale);
            y = ToPhysical(p.Y, scale);
        }
        else if (targetScreen is not null)
        {
            // CenterScreen / CenterOwner（没有所有者窗口时等同 CenterScreen）：主显示器工作区居中。
            var work = targetScreen.WorkingArea;
            x = ToPhysical(work.X, scale) + Math.Max(0, (ToPhysical(work.Width, scale) - width) / 2);
            y = ToPhysical(work.Y, scale) + Math.Max(0, (ToPhysical(work.Height, scale) - height) / 2);
        }
        else
        {
            var monitor = User32.MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
            if (monitor != IntPtr.Zero && User32.GetMonitorInfoW(monitor, &info) != 0)
            {
                var work = info.rcWork;
                x = work.Left + Math.Max(0, (work.Width - width) / 2);
                y = work.Top + Math.Max(0, (work.Height - height) / 2);
            }
            else
            {
                flags |= SWP_NOMOVE;
            }
        }
        User32.SetWindowPos(_hwnd, IntPtr.Zero, x, y, width, height, flags);
    }

    /// <summary>窗口移动/缩放/DPI 变化后刷新无障碍边界换算的客户区原点。</summary>
    void RefreshAccessibilityGeometry()
    {
        if (_accessibility is null || _closed || _hwnd == IntPtr.Zero) return;
        _accessibility.RefreshGeometry(_hwnd, RenderScale);
    }

    void UpdateClientSize()
    {
        RECT rc;
        if (User32.GetClientRect(_hwnd, &rc) != 0) _clientPx = new PixelSize(rc.Width, rc.Height);
    }

    void CheckResize()
    {
        var size = ClientSize;
        double scale = RenderScale;
        if (size == _lastSize && scale == _lastScale) return;
        _lastSize = size;
        _lastScale = scale;
        Events?.OnResized(size, scale);
    }

    /// <summary>WM_GETMINMAXINFO：把逻辑像素的最小/最大尺寸换算为窗口的物理轨迹尺寸。</summary>
    void ApplyMinMaxInfo(MINMAXINFO* info)
    {
        if (info == null) return;
        double scale = RenderScale;
        if (_minWidth.HasValue) info->ptMinTrackSize.X = Math.Max(0, ToPhysical(_minWidth.Value, scale));
        if (_minHeight.HasValue) info->ptMinTrackSize.Y = Math.Max(0, ToPhysical(_minHeight.Value, scale));
        if (_maxWidth.HasValue) info->ptMaxTrackSize.X = Math.Max(0, ToPhysical(_maxWidth.Value, scale));
        if (_maxHeight.HasValue) info->ptMaxTrackSize.Y = Math.Max(0, ToPhysical(_maxHeight.Value, scale));
        if (info->ptMaxTrackSize.X < info->ptMinTrackSize.X) info->ptMaxTrackSize.X = info->ptMinTrackSize.X;
        if (info->ptMaxTrackSize.Y < info->ptMinTrackSize.Y) info->ptMaxTrackSize.Y = info->ptMinTrackSize.Y;
    }

    /// <summary>用户经标题栏/系统菜单改变状态（WM_SIZE）、或平台进入/退出全屏时报告。</summary>
    void RaiseStateChangedIfNeeded()
    {
        if (_closed || _hwnd == IntPtr.Zero) return;
        var state = WindowState;
        if (state == _lastReportedState) return;
        _lastReportedState = state;
        (Events as IWindowStateEvents)?.OnWindowStateChanged(state);
    }

    /// <summary>用户拖动窗口或系统移动窗口时报告位置；只在逻辑坐标变化时触发。</summary>
    void RaisePositionChangedIfNeeded()
    {
        if (_closed || _hwnd == IntPtr.Zero) return;
        var position = Position;
        if (_hasWindowPosition && position == _lastWindowPosition) return;
        _hasWindowPosition = true;
        _lastWindowPosition = position;
        (Events as IWindowStateEvents)?.OnPositionChanged(position);
    }

    /// <summary>WM_ACTIVATE / WM_ACTIVATEAPP 的去重激活回调。</summary>
    void RaiseActivated(bool active)
    {
        if (_hasReportedActivation && _lastActivation == active) return;
        _hasReportedActivation = true;
        _lastActivation = active;
        (Events as IWindowLifecycleEvents)?.OnActivated(active);
    }

    // ---------------------------------------------------------------- 窗口过程

    internal void Attach(IntPtr hwnd) => _hwnd = hwnd;

    internal IntPtr WndProc(uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 回调里可能销毁窗口（_hwnd 随之清零），缺省处理仍使用本条消息的句柄
        var hwnd = _hwnd;
        if (msg >= WM_IME_STARTCOMPOSITION && msg <= WM_IME_COMPOSITION || msg == WM_IME_SETCONTEXT)
        {
            if (_textInput is { } ime && ime.HandleMessage(hwnd, msg, wParam, ref lParam, out var imeResult)) return imeResult;
            return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
        }
        switch (msg)
        {
            case WM_PAINT:
                Paint();
                return IntPtr.Zero;

            case WM_ERASEBKGND:
                return new IntPtr(1); // 整个客户区都由帧缓冲覆盖，跳过擦除以免闪烁

            case WM_MOVE:
                RaisePositionChangedIfNeeded();
                RefreshAccessibilityGeometry();
                return IntPtr.Zero;

            case WM_SIZE:
                // 最小化时客户区为 0×0：保留原尺寸，避免帧缓冲来回重建
                if ((int)(long)wParam != SIZE_MINIMIZED)
                {
                    _clientPx = new PixelSize(LoWord(lParam), HiWord(lParam));
                    CheckResize();
                    RaisePositionChangedIfNeeded();
                    RefreshAccessibilityGeometry();
                }
                RaiseStateChangedIfNeeded();
                return IntPtr.Zero;

            case WM_SHOWWINDOW:
                if ((long)wParam != 0)
                {
                    RaisePositionChangedIfNeeded();
                    RefreshAccessibilityGeometry();
                }
                break;

            case WM_ACTIVATE:
            {
                bool active = LoWord(wParam) != WA_INACTIVE;
                RaiseActivated(active);
                (Events as IWindowLifecycleEvents)?.OnLifecycle(active ? AppLifecycle.Active : AppLifecycle.Inactive);
                RaisePositionChangedIfNeeded();
                break;
            }

            case WM_ACTIVATEAPP:
            {
                bool appActive = (long)wParam != 0;
                RaiseActivated(appActive);
                (Events as IWindowLifecycleEvents)?.OnLifecycle(appActive ? AppLifecycle.Foreground : AppLifecycle.Background);
                break;
            }

            case WM_GETMINMAXINFO:
                ApplyMinMaxInfo((MINMAXINFO*)lParam.ToPointer());
                return IntPtr.Zero;

            case WM_DISPLAYCHANGE:
                _platform.RefreshScreens();
                return IntPtr.Zero;

            case WM_DPICHANGED:
            {
                _dpi = (uint)LoWord(wParam); // LOWORD = X 轴 DPI，HIWORD = Y 轴 DPI（二者相同）
                var suggested = (RECT*)lParam.ToPointer();
                User32.SetWindowPos(_hwnd, IntPtr.Zero, suggested->Left, suggested->Top, suggested->Width, suggested->Height,
                    SWP_NOZORDER | SWP_NOACTIVATE);
                UpdateClientSize();
                CheckResize();
                RaisePositionChangedIfNeeded();
                RefreshAccessibilityGeometry();
                RequestFrame();
                return IntPtr.Zero;
            }

            case WM_SETCURSOR:
                if (LoWord(lParam) == HTCLIENT)
                {
                    User32.SetCursor(_cursor);
                    return new IntPtr(1);
                }
                break;

            case WM_MOUSEMOVE:
                OnMouseMove(wParam, lParam);
                return IntPtr.Zero;

            case WM_MOUSELEAVE:
                if (!_tracking) return IntPtr.Zero; // 已在释放捕获时处理过
                _tracking = false;
                RaisePointer(RawPointerEventType.Leave, PointerButtons.None, _buttons, _lastPosition);
                return IntPtr.Zero;

            case WM_LBUTTONDOWN: OnButton(true, PointerButtons.Left, wParam, lParam); return IntPtr.Zero;
            case WM_LBUTTONUP: OnButton(false, PointerButtons.Left, wParam, lParam); return IntPtr.Zero;
            case WM_RBUTTONDOWN: OnButton(true, PointerButtons.Right, wParam, lParam); return IntPtr.Zero;
            case WM_RBUTTONUP: OnButton(false, PointerButtons.Right, wParam, lParam); return IntPtr.Zero;
            case WM_MBUTTONDOWN: OnButton(true, PointerButtons.Middle, wParam, lParam); return IntPtr.Zero;
            case WM_MBUTTONUP: OnButton(false, PointerButtons.Middle, wParam, lParam); return IntPtr.Zero;
            case WM_XBUTTONDOWN:
            case WM_XBUTTONUP:
            {
                int which = HiWord(wParam); // GET_XBUTTON_WPARAM
                var button = which == XBUTTON1 ? PointerButtons.X1 : which == XBUTTON2 ? PointerButtons.X2 : PointerButtons.None;
                if (button != PointerButtons.None) OnButton(msg == WM_XBUTTONDOWN, button, wParam, lParam);
                return new IntPtr(1); // 文档要求 X 按钮消息返回 TRUE
            }

            case WM_MOUSEWHEEL:
            case WM_MOUSEHWHEEL:
                OnWheel(msg == WM_MOUSEHWHEEL, wParam, lParam);
                return IntPtr.Zero;

            case WM_SETTINGCHANGE:
                // 外观（ImmersiveColorSet）、辅助功能与滚轮行数等系统设置变化
                _platform.RefreshPreferences();
                if ((uint)(long)wParam is SPI_SETWHEELSCROLLLINES or SPI_SETWHEELSCROLLCHARS) _wheelSettingsRead = false;
                if ((uint)(long)wParam == SPI_SETWORKAREA) _platform.RefreshScreens();
                return User32.DefWindowProcW(hwnd, msg, wParam, lParam);

            case WM_THEMECHANGED:
                _platform.RefreshPreferences();
                break;

            case WM_CAPTURECHANGED:
                // 捕获被其他窗口夺走（如弹出菜单、Alt+Tab）且仍有按键按下：取消当前指针序列
                if (lParam != _hwnd && _buttons != PointerButtons.None)
                {
                    _buttons = PointerButtons.None;
                    RaisePointer(RawPointerEventType.Cancel, PointerButtons.None, PointerButtons.None, _lastPosition);
                }
                return IntPtr.Zero;

            case WM_KEYDOWN:
            case WM_KEYUP:
            case WM_SYSKEYDOWN:
            case WM_SYSKEYUP:
                if (msg == WM_KEYUP) _ignoreWmChar = false; // 上一次输入法提交之后的那一个 WM_CHAR 已经过去
                OnKey(msg, wParam, lParam);
                // WM_SYS* 交给系统处理 Alt+F4、Alt+空格 等
                if (msg == WM_SYSKEYDOWN || msg == WM_SYSKEYUP) break;
                return IntPtr.Zero;

            case WM_CHAR:
                // 组合中的 WM_CHAR（拼音等）与结果串的重复投递不交给客户端
                if (!ShouldIgnoreChar()) OnChar(wParam, lParam);
                return IntPtr.Zero;

            case WM_IME_CHAR:
                // 输入法的字符已经由 WM_IME_COMPOSITION / WM_CHAR 处理；交给 DefWindowProc 会再生成一个 WM_CHAR
                return IntPtr.Zero;

            case WM_INPUTLANGCHANGE:
                // 切换输入语言：丢弃半个代理对与待丢弃的 WM_CHAR，并清除旧布局残留的死键
                ResetInputState();
                return User32.DefWindowProcW(hwnd, msg, wParam, lParam);

            case WM_SYSCHAR:
                // Alt+字母：没有菜单可匹配，交给系统只会响起提示音；仅 Alt+空格 保留给系统菜单
                if ((long)wParam == ' ') break;
                return IntPtr.Zero;

            case WM_SETFOCUS:
                Events?.OnFocusChanged(true);
                _accessibility?.OnWindowFocusChanged(true);
                return IntPtr.Zero;

            case WM_KILLFOCUS:
                _chars.Reset();
                _ignoreWmChar = false;
                Events?.OnFocusChanged(false);
                _accessibility?.OnWindowFocusChanged(false);
                return IntPtr.Zero;

            case Win32UiaConstants.WmGetObject:
                // UIA 根对象：把 COM provider 交给系统；其他对象（MSAA）仍走 DefWindowProc。
                if (_accessibility is not null && Win32AccessibilityBridge.IsRootObjectRequest(lParam))
                {
                    var accessibilityResult = _accessibility.HandleGetObject(hwnd, wParam, lParam);
                    if (accessibilityResult != IntPtr.Zero) return accessibilityResult;
                }
                break;

            case WM_CLOSE:
                // 用户点击关闭按钮 / Alt+F4：询问框架，同意后销毁窗口
                if (Events?.OnCloseRequested() != false) User32.DestroyWindow(_hwnd);
                return IntPtr.Zero;

            case WM_DESTROY:
                OnDestroyed();
                return IntPtr.Zero;

            case WM_NCDESTROY:
            {
                var result = User32.DefWindowProcW(hwnd, msg, wParam, lParam);
                _platform.Unregister(hwnd);
                _hwnd = IntPtr.Zero;
                return result;
            }
        }
        return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    void Paint()
    {
        CheckResize();
        _inPaint = true;
        try
        {
            Events?.OnFrame(_platform.Clock.Elapsed);
        }
        finally
        {
            _inPaint = false;
            // 无论回调是否抛出异常都必须 BeginPaint/EndPaint 验证更新区域，否则 WM_PAINT 会无限重发
            PAINTSTRUCT ps;
            var hdc = User32.BeginPaint(_hwnd, &ps);
            if (hdc != IntPtr.Zero)
            {
                _surface.Blit(hdc, &ps.rcPaint);
                User32.EndPaint(_hwnd, &ps);
            }
        }

        if (_framePending && !_closed)
        {
            _framePending = false;
            // 连续动画：等到 DWM 下一次合成再请求下一帧，帧率跟随显示器刷新率
            Win32Platform.WaitForVBlank();
            User32.InvalidateRect(_hwnd, null, 0);
        }
    }

    /// <summary>绘制回调之外渲染完成（例如框架主动渲染）时立即呈现，不再触发 OnFrame。</summary>
    internal void PresentOutsidePaint()
    {
        if (_inPaint || _closed || _hwnd == IntPtr.Zero) return;
        var hdc = User32.GetDC(_hwnd);
        if (hdc == IntPtr.Zero) return;
        _surface.Blit(hdc, null);
        User32.ReleaseDC(_hwnd, hdc);
    }

    void OnDestroyed()
    {
        if (_closed) return;
        _closed = true;
        _framePending = false;
        _buttons = PointerButtons.None;
        _tracking = false;
        // 窗口销毁前解除输入法关联并销毁自建的输入上下文
        _textInput?.Release(_hwnd);
        // 无障碍桥先断开 UIA，再释放 provider 引用
        _accessibility?.Dispose();
        _accessibility = null;
        if (_windowIcon != IntPtr.Zero)
        {
            User32.DestroyIcon(_windowIcon);
            _windowIcon = IntPtr.Zero;
        }
        try
        {
            Events?.OnClosed();
        }
        finally
        {
            _surface.Dispose();
            _platform.OnWindowClosed(this);
        }
    }

    // ---------------------------------------------------------------- 输入

    static int LoWord(IntPtr value) => (int)((long)value & 0xFFFF);
    static int HiWord(IntPtr value) => (int)(((long)value >> 16) & 0xFFFF);

    /// <summary>GET_X_LPARAM / GET_Y_LPARAM：多显示器下可为负，必须按有符号 16 位解释。</summary>
    Point ClientPoint(int x, int y)
    {
        double scale = RenderScale;
        return new Point((float)(x / scale), (float)(y / scale));
    }

    Point PositionFromLParam(IntPtr lParam) => ClientPoint((short)LoWord(lParam), (short)HiWord(lParam));

    /// <summary>物理像素坐标是否在客户区内（捕获期间指针可在窗口外）。</summary>
    bool IsInsideClient(IntPtr lParam)
    {
        int x = (short)LoWord(lParam), y = (short)HiWord(lParam);
        return x >= 0 && y >= 0 && x < _clientPx.Width && y < _clientPx.Height;
    }

    void StopTracking()
    {
        var tme = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = TME_CANCEL | TME_LEAVE, hwndTrack = _hwnd };
        User32.TrackMouseEvent(&tme);
        _tracking = false;
    }

    static PointerButtons ButtonsFromMk(int mk)
    {
        var b = PointerButtons.None;
        if ((mk & MK_LBUTTON) != 0) b |= PointerButtons.Left;
        if ((mk & MK_RBUTTON) != 0) b |= PointerButtons.Right;
        if ((mk & MK_MBUTTON) != 0) b |= PointerButtons.Middle;
        if ((mk & MK_XBUTTON1) != 0) b |= PointerButtons.X1;
        if ((mk & MK_XBUTTON2) != 0) b |= PointerButtons.X2;
        return b;
    }

    static TimeSpan MessageTime() => TimeSpan.FromMilliseconds((uint)User32.GetMessageTime());

    void RaisePointer(RawPointerEventType type, PointerButtons changed, PointerButtons buttons, Point position, float dx = 0, float dy = 0)
    {
        var events = Events;
        if (events == null) return;
        float pressure = type == RawPointerEventType.Wheel || (type == RawPointerEventType.Move && buttons == PointerButtons.None) ? 0f : 0.5f;
        events.OnPointer(new RawPointerEvent(type, PointerType.Mouse, 0, position, changed, buttons, KeyMap.Modifiers(),
            MessageTime(), pressure, dx, dy, preciseWheel: false));
    }

    void OnMouseMove(IntPtr wParam, IntPtr lParam)
    {
        var pos = PositionFromLParam(lParam);
        _buttons = ButtonsFromMk(LoWord(wParam));
        // 捕获拖动到窗口外时不开始跟踪：对不在窗口上的指针调用 TrackMouseEvent 会立即产生 WM_MOUSELEAVE
        if (!_tracking && IsInsideClient(lParam))
        {
            // 请求在指针离开客户区时收到一次 WM_MOUSELEAVE（每次离开后需重新请求）
            var tme = new TRACKMOUSEEVENT { cbSize = (uint)sizeof(TRACKMOUSEEVENT), dwFlags = TME_LEAVE, hwndTrack = _hwnd };
            if (User32.TrackMouseEvent(&tme) != 0) _tracking = true;
            RaisePointer(RawPointerEventType.Enter, PointerButtons.None, _buttons, pos);
        }
        _lastPosition = pos;
        RaisePointer(RawPointerEventType.Move, PointerButtons.None, _buttons, pos);
    }

    void OnButton(bool down, PointerButtons button, IntPtr wParam, IntPtr lParam)
    {
        var pos = PositionFromLParam(lParam);
        _lastPosition = pos;
        _buttons = ButtonsFromMk(LoWord(wParam));
        if (down) _buttons |= button;
        else _buttons &= ~button;

        // 按下时捕获鼠标，拖出窗口也能收到移动与抬起；全部抬起后释放
        if (down && User32.GetCapture() != _hwnd) User32.SetCapture(_hwnd);
        RaisePointer(down ? RawPointerEventType.Down : RawPointerEventType.Up, button, _buttons, pos);
        if (_closed || down || _buttons != PointerButtons.None) return;
        if (User32.GetCapture() == _hwnd) User32.ReleaseCapture();
        // 在窗口外抬起：捕获期间不一定收到 WM_MOUSELEAVE，这里补发离开
        if (_tracking && !IsInsideClient(lParam))
        {
            StopTracking();
            RaisePointer(RawPointerEventType.Leave, PointerButtons.None, PointerButtons.None, pos);
        }
    }

    readonly Win32WheelTranslator _wheel = new();
    bool _wheelSettingsRead;

    /// <summary>读取系统的滚轮设置（每格行数 / 字符数）；启动时与 WM_SETTINGCHANGE 后读取。</summary>
    void ReadWheelSettings()
    {
        _wheelSettingsRead = true;
        uint lines = 0, chars = 0;
        if (User32.SystemParametersInfoW(SPI_GETWHEELSCROLLLINES, 0, &lines, 0) != 0) _wheel.LinesPerNotch = lines;
        if (User32.SystemParametersInfoW(SPI_GETWHEELSCROLLCHARS, 0, &chars, 0) != 0) _wheel.CharsPerNotch = chars;
    }

    void OnWheel(bool horizontal, IntPtr wParam, IntPtr lParam)
    {
        if (!_wheelSettingsRead) ReadWheelSettings();
        // 滚轮消息的坐标是屏幕坐标
        var pt = new POINT { X = (short)LoWord(lParam), Y = (short)HiWord(lParam) };
        User32.ScreenToClient(_hwnd, &pt);
        var pos = ClientPoint(pt.X, pt.Y);
        _buttons = ButtonsFromMk(LoWord(wParam));
        int raw = (short)HiWord(wParam); // GET_WHEEL_DELTA_WPARAM
        // 符号约定与 macOS 后端一致：dy > 0 向上滚动；dx > 0 向左滚动（换算见 Input/Win32Wheel.cs）
        if (!_wheel.Translate(horizontal, raw, ClientSize, out float dx, out float dy, out var mode, out bool precise)) return;
        Events?.OnPointer(RawPointerEvent.CreateWheel(pos, dx, dy, mode, precise, ScrollPhase.None, ScrollPhase.None, _buttons,
            KeyMap.Modifiers(), MessageTime()));
    }

    void OnKey(uint msg, IntPtr wParam, IntPtr lParam)
    {
        var events = Events;
        if (events == null) return;
        long lp = (long)lParam;
        int vk = (int)((long)wParam & 0xFFFF);
        // 输入法正在处理的按键（组合中）：属于输入法，不交给框架（与浏览器的 keyCode 229 一致）
        if (vk == VK_PROCESSKEY) return;
        int code = KeyMap.ScanCode(lp, vk);
        var physical = KeyMap.Get(code);
        bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
        bool repeat = down && (lp & (1L << 30)) != 0; // 第 30 位：消息之前该键已按下
        TranslateKey(vk, code, physical, out var logical, out var mods);
        events.OnKey(new RawKeyEvent(down ? RawKeyEventType.Down : RawKeyEventType.Up, physical, mods, repeat, code, MessageTime(), logical));
    }

    /// <summary>
    /// 逻辑键与修饰键：非字符键取虚拟键的键名；字符键用 ToUnicodeEx 按当前布局求出（去掉 Ctrl / Alt 的状态，所以 Ctrl+C 的逻辑键是 c）。
    /// Ctrl 与 Alt 同时按下且在该状态下产生字符时，这是 AltGr（或 Ctrl+Alt 代替 AltGr）：逻辑键取产生的字符，修饰键报告 AltGraph。
    /// ToUnicodeEx 使用 flags = 4（不改变键盘状态、不消耗死键，Windows 10 1607+）。
    /// </summary>
    static void TranslateKey(int vk, int scanCode, PhysicalKey physical, out string? logical, out KeyModifiers mods)
    {
        bool shift = User32.GetKeyState(VK_SHIFT) < 0;
        bool control = User32.GetKeyState(VK_CONTROL) < 0;
        bool alt = User32.GetKeyState(VK_MENU) < 0;
        bool meta = User32.GetKeyState(VK_LWIN) < 0 || User32.GetKeyState(VK_RWIN) < 0;
        logical = Win32Keys.NamedKey(vk, physical);
        if (vk == VK_MENU && physical == PhysicalKey.AltRight && User32.GetKeyState(VK_LCONTROL) < 0) logical = KeyValues.AltGraph;
        if (logical is not null)
        {
            mods = Win32Keys.ResolveModifiers(shift, control, alt, meta, producesTextWithCtrlAlt: false);
            return;
        }
        var state = stackalloc byte[256];
        var buffer = new char[8];
        bool altGrText = false;
        if (User32.GetKeyboardState(state) != 0)
        {
            var layout = User32.GetKeyboardLayout(0);
            uint sc = (uint)(scanCode & 0xFF);
            if (control && alt)
            {
                fixed (char* b = buffer)
                {
                    int r = User32.ToUnicodeEx((uint)vk, sc, state, b, buffer.Length, 4, layout);
                    var withAltGr = Win32Keys.FromToUnicode(r, buffer);
                    if (withAltGr is not null)
                    {
                        altGrText = true;
                        logical = withAltGr;
                    }
                }
            }
            if (!altGrText)
            {
                // 去掉 Ctrl 与 Alt 求逻辑键
                state[VK_CONTROL] = state[VK_LCONTROL] = state[VK_RCONTROL] = 0;
                state[VK_MENU] = state[VK_LMENU] = state[VK_RMENU] = 0;
                fixed (char* b = buffer)
                {
                    int r = User32.ToUnicodeEx((uint)vk, sc, state, b, buffer.Length, 4, layout);
                    logical = Win32Keys.FromToUnicode(r, buffer);
                }
            }
        }
        mods = Win32Keys.ResolveModifiers(shift, control, alt, meta, altGrText);
        logical ??= KeyValues.FromPhysicalKey(physical, mods);
    }

    /// <summary>输入法刚提交了结果串：抑制随后由 IMM32 重复投递的 WM_CHAR（下一次 WM_KEYUP 清除）。</summary>
    internal void IgnoreNextChar() => _ignoreWmChar = true;

    /// <summary>组合期间的 WM_CHAR（组合中的拼音、结果串的重复投递）不交给客户端。</summary>
    bool ShouldIgnoreChar() => _ignoreWmChar || _textInput?.Client?.Composition is not null;

    /// <summary>切换输入语言后丢弃陈旧的输入状态：半个代理对、待丢弃的 WM_CHAR，以及旧布局残留的死键。</summary>
    void ResetInputState()
    {
        _chars.Reset();
        _ignoreWmChar = false;
        FlushDeadKey();
    }

    /// <summary>
    /// 消费键盘布局中尚未落定的死键。ToUnicodeEx 的 flags = 0（而不是 TranslateKey 使用的 4）会改变内核状态：
    /// 用一个空格把死键组合掉，结果本身丢弃。切换输入语言后，旧布局的死键不会带进新布局。
    /// </summary>
    static void FlushDeadKey()
    {
        var state = stackalloc byte[256];
        if (User32.GetKeyboardState(state) == 0) return;
        var buffer = new char[8];
        uint scan = User32.MapVirtualKeyW(VK_SPACE, MAPVK_VK_TO_VSC_EX);
        fixed (char* p = buffer)
            User32.ToUnicodeEx(VK_SPACE, scan, state, p, buffer.Length, 0, User32.GetKeyboardLayout(0));
    }

    void OnChar(IntPtr wParam, IntPtr lParam)
    {
        var text = _chars.Push((char)((long)wParam & 0xFFFF));
        if (text is null) return;
        int repeat = Math.Max(1, LoWord(lParam)); // 第 0–15 位：重复次数
        for (int i = 0; i < repeat && !_closed; i++)
        {
            // 有文本输入客户端时交给它（与输入法的提交同一路径），否则经 OnTextInput
            if (_textInput?.Client is { } client) client.Commit(text);
            else Events?.OnTextInput(text);
        }
    }

    // ---------------------------------------------------------------- 帧缓冲

    /// <summary>
    /// 软件呈现用的帧缓冲：DIB section（32 位 BI_RGB，biHeight 为负表示自上而下），内存顺序 B,G,R,X，
    /// 渲染器直接按 <see cref="PixelFormat.Bgra8888"/> 写入，无需转换。DIB 常驻在一个内存 DC 中供 BitBlt。
    /// </summary>
    sealed class Win32Surface : NativeSurface, IFramebufferSurface, IDisposable
    {
        readonly Win32Window _owner;
        IntPtr _memDC;
        IntPtr _bitmap;
        IntPtr _oldBitmap;
        IntPtr _bits;
        PixelSize _size;

        public Win32Surface(Win32Window owner) => _owner = owner;

        public override PixelSize Size => _owner._clientPx;
        public override double Scale => _owner.RenderScale;

        public ILockedFramebuffer Lock()
        {
            if (_owner._closed) throw new ObjectDisposedException(nameof(Win32Window));
            var size = _owner._clientPx;
            if (size.IsEmpty) size = new PixelSize(1, 1);
            if (_bitmap == IntPtr.Zero || _size != size) Recreate(size);
            // CPU 写入 DIB 前必须确保 GDI 已完成对它的所有批处理操作
            Gdi32.GdiFlush();
            return new Locked(this, _bits, _size);
        }

        void Recreate(PixelSize size)
        {
            if (_memDC == IntPtr.Zero)
            {
                _memDC = Gdi32.CreateCompatibleDC(IntPtr.Zero);
                if (_memDC == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleDC failed.");
            }
            ReleaseBitmap();

            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)sizeof(BITMAPINFOHEADER),
                    biWidth = size.Width,
                    biHeight = -size.Height, // 负值：自上而下，第 0 行在内存最前
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB,
                },
            };
            void* bits;
            var bitmap = Gdi32.CreateDIBSection(_memDC, &info, DIB_RGB_COLORS, &bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == null)
                throw new OutOfMemoryException($"CreateDIBSection failed for {size.Width}x{size.Height}.");
            _bitmap = bitmap;
            _bits = (IntPtr)bits;
            _size = size;
            _oldBitmap = Gdi32.SelectObject(_memDC, _bitmap);
        }

        void ReleaseBitmap()
        {
            if (_bitmap == IntPtr.Zero) return;
            Gdi32.SelectObject(_memDC, _oldBitmap);
            Gdi32.DeleteObject(_bitmap);
            _bitmap = IntPtr.Zero;
            _bits = IntPtr.Zero;
            _size = default;
        }

        /// <summary>把帧缓冲 1:1 拷贝到窗口；尚无帧缓冲时用黑色填充，避免露出未初始化内容。</summary>
        public void Blit(IntPtr hdc, RECT* paintRect)
        {
            if (_bitmap == IntPtr.Zero)
            {
                RECT rc;
                if (paintRect == null)
                {
                    rc = new RECT(0, 0, _owner._clientPx.Width, _owner._clientPx.Height);
                    paintRect = &rc;
                }
                User32.FillRect(hdc, paintRect, Gdi32.GetStockObject(BLACK_BRUSH));
                return;
            }
            Gdi32.BitBlt(hdc, 0, 0, _size.Width, _size.Height, _memDC, 0, 0, SRCCOPY);
        }

        public void Dispose()
        {
            ReleaseBitmap();
            if (_memDC != IntPtr.Zero)
            {
                Gdi32.DeleteDC(_memDC);
                _memDC = IntPtr.Zero;
            }
        }

        sealed class Locked : ILockedFramebuffer
        {
            readonly Win32Surface _surface;
            public Locked(Win32Surface surface, IntPtr address, PixelSize size)
            {
                _surface = surface;
                Address = address;
                Size = size;
            }

            public IntPtr Address { get; }
            public PixelSize Size { get; }
            public int RowBytes => Size.Width * 4; // 32 位 DIB 每行天然 4 字节对齐
            public PixelFormat Format => PixelFormat.Bgra8888;

            // 在 WM_PAINT 之外绘制时立即呈现新内容（WM_PAINT 中由 Paint 统一 BitBlt）
            public void Dispose() => _surface._owner.PresentOutsidePaint();
        }
    }
}
