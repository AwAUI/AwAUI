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
public sealed unsafe class Win32Window : WindowImpl
{
    /// <summary>Windows 桌面使用常驻的经典滚动条。</summary>
    public override ScrollbarStyle PreferredScrollbarStyle => ScrollbarStyle.Classic;

    readonly Win32Platform _platform;
    readonly Win32Surface _surface;
    readonly uint _style;
    readonly uint _exStyle;

    IntPtr _hwnd;
    uint _dpi = USER_DEFAULT_SCREEN_DPI;
    PixelSize _clientPx;
    Size _lastSize;
    double _lastScale;
    bool _inPaint;
    bool _framePending;
    bool _closed;
    bool _tracking;
    PointerButtons _buttons;
    Point _lastPosition;
    Utf16Assembler _chars;
    Win32TextInput? _textInput;
    IntPtr _cursor;

    internal Win32Window(Win32Platform platform, WindowOptions options)
    {
        _platform = platform;
        _surface = new Win32Surface(this);
        _style = options.Resizable ? WS_OVERLAPPEDWINDOW : WS_OVERLAPPEDWINDOW & ~(WS_THICKFRAME | WS_MAXIMIZEBOX);
        _exStyle = 0;
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

        // 窗口创建后才能知道它所在显示器的 DPI：据此把逻辑客户区尺寸换算为窗口尺寸，并在工作区居中
        _dpi = Win32Platform.GetWindowDpi(hwnd);
        SetInitialBounds(options.Size);
        UpdateClientSize();
        _lastSize = ClientSize;
        _lastScale = RenderScale;
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

    /// <summary>HWND 句柄（窗口销毁后为 <see cref="IntPtr.Zero"/>）。</summary>
    public IntPtr Handle => _hwnd;

    /// <summary>输入法桥（IMM32）。</summary>
    public override TextInputMethod TextInput => _textInput ??= new Win32TextInput(this);

    /// <summary>平台剪贴板（CF_UNICODETEXT）。</summary>
    public override Clipboard Clipboard => _platform.Clipboard;

    Win32TextInput Ime => (Win32TextInput)TextInput;

    /// <summary>客户区的设备像素尺寸（帧缓冲尺寸以此为准，避免逻辑尺寸往返换算的舍入误差）。</summary>
    internal PixelSize ClientPixelSize => _clientPx;

    public override void Show()
    {
        if (_closed) return;
        if (!_platform.Dispatcher.CheckAccess()) { _platform.Dispatcher.Post(Show); return; }
        User32.ShowWindow(_hwnd, SW_SHOWNORMAL);
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

    void SetInitialBounds(Size logical)
    {
        double scale = RenderScale;
        var r = new RECT(0, 0, (int)Math.Round(logical.Width * scale), (int)Math.Round(logical.Height * scale));
        Win32Platform.AdjustWindowRect(&r, _style, _exStyle, _dpi);
        int width = r.Width, height = r.Height;

        uint flags = SWP_NOZORDER | SWP_NOACTIVATE;
        int x = 0, y = 0;
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
        User32.SetWindowPos(_hwnd, IntPtr.Zero, x, y, width, height, flags);
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

            case WM_SIZE:
                // 最小化时客户区为 0×0：保留原尺寸，避免帧缓冲来回重建
                if ((int)(long)wParam != SIZE_MINIMIZED)
                {
                    _clientPx = new PixelSize(LoWord(lParam), HiWord(lParam));
                    CheckResize();
                }
                return IntPtr.Zero;

            case WM_DPICHANGED:
            {
                _dpi = (uint)LoWord(wParam); // LOWORD = X 轴 DPI，HIWORD = Y 轴 DPI（二者相同）
                var suggested = (RECT*)lParam.ToPointer();
                User32.SetWindowPos(_hwnd, IntPtr.Zero, suggested->Left, suggested->Top, suggested->Width, suggested->Height,
                    SWP_NOZORDER | SWP_NOACTIVATE);
                UpdateClientSize();
                CheckResize();
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
                // 滚轮行数等系统设置变化：下次滚动时重新读取
                if ((uint)(long)wParam is SPI_SETWHEELSCROLLLINES or SPI_SETWHEELSCROLLCHARS) _wheelSettingsRead = false;
                return User32.DefWindowProcW(hwnd, msg, wParam, lParam);

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
                OnKey(msg, wParam, lParam);
                // WM_SYS* 交给系统处理 Alt+F4、Alt+空格 等
                if (msg == WM_SYSKEYDOWN || msg == WM_SYSKEYUP) break;
                return IntPtr.Zero;

            case WM_CHAR:
                OnChar(wParam, lParam);
                return IntPtr.Zero;

            case WM_SYSCHAR:
                // Alt+字母：没有菜单可匹配，交给系统只会响起提示音；仅 Alt+空格 保留给系统菜单
                if ((long)wParam == ' ') break;
                return IntPtr.Zero;

            case WM_SETFOCUS:
                Events?.OnFocusChanged(true);
                return IntPtr.Zero;

            case WM_KILLFOCUS:
                _chars.Reset();
                Events?.OnFocusChanged(false);
                return IntPtr.Zero;

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
