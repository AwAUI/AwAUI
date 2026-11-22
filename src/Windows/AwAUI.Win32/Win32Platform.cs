using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AwAUI.Platform;
using AwAUI.Threading;
using AwAUI.Win32.Interop;
using static AwAUI.Win32.Interop.Const;

namespace AwAUI.Win32;

/// <summary>
/// Windows 平台后端：blittable P/Invoke 直接调用 user32 / gdi32（design/04）。
/// 必须在将要运行消息循环的线程上创建；窗口、调度器、消息循环都绑定到该线程。
/// M0 采用软件呈现：帧缓冲是 DIB section，WM_PAINT 中 BitBlt 到窗口。
/// </summary>
public sealed unsafe class Win32Platform : PlatformBackend
{
    /// <summary>私有消息：执行调度器队列。</summary>
    internal const uint WM_AWAUI_DRAIN = WM_APP + 1;
    /// <summary>私有消息：从其他线程请求退出消息循环。</summary>
    internal const uint WM_AWAUI_EXIT = WM_APP + 2;

    const string WindowClassName = "AwAUIWindow";
    const string MessageClassName = "AwAUIMessageWindow";

    static Win32Platform? s_instance;

    readonly Win32Dispatcher _dispatcher;
    readonly Dictionary<IntPtr, Win32Window> _windowsByHandle = new();
    readonly List<Win32Window> _windows = new();
    readonly IntPtr _messageWindow;
    internal readonly IntPtr Instance;
    internal readonly ushort WindowClass;
    internal readonly Stopwatch Clock = Stopwatch.StartNew();
    Win32Window? _creating;
    bool _running;
    readonly Win32AppearancePreference _appearance = new();
    readonly Win32ReducedMotionPreference _reducedMotion = new();

    public Win32Platform()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) throw new PlatformNotSupportedException("Win32Platform requires Windows.");
        if (s_instance != null) throw new InvalidOperationException("Only one Win32Platform may be created per process.");

        // 必须在创建任何窗口之前设置
        EnableDpiAwareness();

        Instance = Kernel32.GetModuleHandleW(null);
        var appIcon = User32.LoadIconW(IntPtr.Zero, new IntPtr(IDI_APPLICATION));
        WindowClass = RegisterClass(WindowClassName, CS_HREDRAW | CS_VREDRAW, User32.LoadCursorW(IntPtr.Zero, new IntPtr(IDC_ARROW)), appIcon);
        ushort messageClass = RegisterClass(MessageClassName, 0, IntPtr.Zero, IntPtr.Zero);

        // 先赋值 s_instance，窗口过程才能把消息路由到这里；仅消息窗口在创建期间的消息走 DefWindowProc
        s_instance = this;
        _messageWindow = User32.CreateWindowExW(0, (char*)messageClass, null, 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, Instance, IntPtr.Zero);
        if (_messageWindow == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            s_instance = null;
            throw new InvalidOperationException($"CreateWindowExW (message window) failed, error {error}.");
        }
        _dispatcher = new Win32Dispatcher(_messageWindow);
        RefreshPreferences();
    }

    internal IAppearancePreference AppearancePreference => _appearance;
    internal IReducedMotionPreference ReducedMotionPreference => _reducedMotion;

    /// <summary>重新读取外观与辅助功能偏好（<see cref="Const.WM_SETTINGCHANGE"/> / <see cref="Const.WM_THEMECHANGED"/> 后）。只报告变化。</summary>
    internal void RefreshPreferences()
    {
        _appearance.Refresh();
        _reducedMotion.Refresh();
    }

    public override string Name => "Win32";

    Win32Clipboard? _clipboard;
    Win32Screens? _screens;
    Win32StorageProvider? _storage;
    Win32Launcher? _launcher;

    /// <summary>系统剪贴板（文本、HTML、图片、文件）。</summary>
    public override Clipboard Clipboard => _clipboard ??= new Win32Clipboard(this);

    /// <summary>显示器集合（Per-Monitor V2）。</summary>
    public override Screens Screens => _screens ??= new Win32Screens();

    /// <summary>文件与目录选择器（comdlg32 / shell32）。</summary>
    public override StorageProvider StorageProvider => _storage ??= new Win32StorageProvider(this);

    /// <summary>打开 URI / 文件 / 目录（ShellExecuteW）。</summary>
    public override Launcher Launcher => _launcher ??= new Win32Launcher(this);

    public override T? GetService<T>() where T : class
    {
        // 与 macOS / Headless 一致：具体类型（Win32Screens、Win32Clipboard……）也能取到，
        // 调用方不必知道服务属性声明成哪个抽象类型。
        if (this is T self) return self;
        return Clipboard as T ?? Screens as T ?? StorageProvider as T ?? Launcher as T;
    }

    /// <summary>显示器布局变化（WM_DISPLAYCHANGE / SPI_SETWORKAREA）后刷新。</summary>
    internal void RefreshScreens() => _screens?.Refresh();

    /// <summary>仅消息窗口（剪贴板的所有者窗口）。</summary>
    internal IntPtr MessageWindow => _messageWindow;
    public override IDispatcher Dispatcher => _dispatcher;
    public IReadOnlyList<Win32Window> Windows => _windows;

    public override WindowImpl CreateWindow(WindowOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (!_dispatcher.CheckAccess()) throw new InvalidOperationException("Windows must be created on the UI thread.");
        var w = new Win32Window(this, options);
        _windows.Add(w);
        return w;
    }

    /// <summary>
    /// 创建窗口的 UI Automation 桥（design/10）。环境变量 <c>AWAUI_WIN32_UIA=0</c> 关闭；
    /// 初始化失败时记录并返回 null，窗口其余功能不受影响。
    /// </summary>
    internal Win32AccessibilityBridge? CreateAccessibilityBridge(Win32Window window)
    {
        if (!Win32AccessibilityBridge.IsEnabled) return null;
        try
        {
            return new Win32AccessibilityBridge(this, window);
        }
        catch (Exception e)
        {
            try { Console.Error.WriteLine("[AwAUI.Win32.UIA] failed to create accessibility bridge: " + e); } catch { }
            return null;
        }
    }

    public override void Run()
    {
        if (!_dispatcher.CheckAccess()) throw new InvalidOperationException("Run must be called on the UI thread.");
        _running = true;
        try
        {
            MSG msg;
            // GetMessageW 返回 0 表示 WM_QUIT，-1 表示错误
            while (User32.GetMessageW(&msg, IntPtr.Zero, 0, 0) > 0)
            {
                User32.TranslateMessage(&msg); // 由 WM_KEYDOWN 生成 WM_CHAR
                User32.DispatchMessageW(&msg);
            }
        }
        finally
        {
            _running = false;
        }
    }

    public override void Exit()
    {
        if (!_dispatcher.CheckAccess())
        {
            User32.PostMessageW(_messageWindow, WM_AWAUI_EXIT, IntPtr.Zero, IntPtr.Zero);
            return;
        }
        if (_running) User32.PostQuitMessage(0);
    }

    // ---------------------------------------------------------------- 窗口登记

    /// <summary>CreateWindowExW 期间到达的消息（WM_NCCREATE、WM_CREATE……）在句柄登记前就会发生，借此关联到正在创建的窗口。</summary>
    internal void BeginCreate(Win32Window window) => _creating = window;
    internal void EndCreate() => _creating = null;

    internal void Unregister(IntPtr hwnd) => _windowsByHandle.Remove(hwnd);

    internal void OnWindowClosed(Win32Window w)
    {
        _windows.Remove(w);
        if (_windows.Count == 0) Exit();
    }

    /// <summary>所有窗口共用的窗口过程（经 <see cref="WndProc"/> 入口进入，异常已在入口处捕获）。</summary>
    internal static IntPtr HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var p = s_instance;
        if (p != null)
        {
            if (hwnd == p._messageWindow)
            {
                if (msg == WM_AWAUI_DRAIN) { p._dispatcher.Drain(); return IntPtr.Zero; }
                if (msg == WM_AWAUI_EXIT) { if (p._running) User32.PostQuitMessage(0); return IntPtr.Zero; }
            }
            else
            {
                if (!p._windowsByHandle.TryGetValue(hwnd, out var window) && p._creating != null)
                {
                    window = p._creating;
                    p._creating = null;
                    window.Attach(hwnd);
                    p._windowsByHandle[hwnd] = window;
                }
                if (window != null) return window.WndProc(msg, wParam, lParam);
            }
        }
        return User32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    ushort RegisterClass(string name, uint style, IntPtr cursor, IntPtr icon)
    {
        fixed (char* pName = name)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = style,
                lpfnWndProc = Interop.WndProc.Pointer,
                hInstance = Instance,
                hIcon = icon,
                hCursor = cursor,
                lpszClassName = pName,
            };
            ushort atom = User32.RegisterClassExW(&wc);
            if (atom == 0)
                throw new InvalidOperationException($"RegisterClassExW({name}) failed, error {Marshal.GetLastWin32Error()}.");
            return atom;
        }
    }

    // ---------------------------------------------------------------- DPI

    static bool s_noGetDpiForWindow, s_noAdjustForDpi, s_noDwmFlush;

    /// <summary>
    /// 按可用性依次尝试：Per-Monitor V2（Win10 1703+）→ Per-Monitor（Win8.1+，shcore）→ System DPI aware（Vista+）。
    /// 若进程已由清单或宿主设置过 DPI 感知，这些调用会失败并保持原设置，属预期行为。
    /// </summary>
    static void EnableDpiAwareness()
    {
        try
        {
            if (User32.SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) != 0) return;
        }
        catch (EntryPointNotFoundException) { }

        try
        {
            Shcore.SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE);
            return; // 返回 E_ACCESSDENIED 表示已设置过，同样不再回退
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }

        try { User32.SetProcessDPIAware(); }
        catch (EntryPointNotFoundException) { }
    }

    internal static uint GetWindowDpi(IntPtr hwnd)
    {
        if (!s_noGetDpiForWindow)
        {
            try
            {
                uint dpi = User32.GetDpiForWindow(hwnd);
                if (dpi != 0) return dpi;
            }
            catch (EntryPointNotFoundException) { s_noGetDpiForWindow = true; }
        }
        // 回退：系统 DPI（Win8.1 的 Per-Monitor 模式随后经 WM_DPICHANGED 校正）
        var dc = User32.GetDC(IntPtr.Zero);
        int sys = dc != IntPtr.Zero ? Gdi32.GetDeviceCaps(dc, LOGPIXELSX) : 0;
        if (dc != IntPtr.Zero) User32.ReleaseDC(IntPtr.Zero, dc);
        return sys > 0 ? (uint)sys : USER_DEFAULT_SCREEN_DPI;
    }

    /// <summary>由客户区矩形计算窗口矩形（含标题栏与边框，按给定 DPI 缩放非客户区）。</summary>
    internal static void AdjustWindowRect(RECT* rect, uint style, uint exStyle, uint dpi)
    {
        if (!s_noAdjustForDpi)
        {
            try
            {
                if (User32.AdjustWindowRectExForDpi(rect, style, 0, exStyle, dpi) != 0) return;
            }
            catch (EntryPointNotFoundException) { s_noAdjustForDpi = true; }
        }
        User32.AdjustWindowRectEx(rect, style, 0, exStyle);
    }

    /// <summary>等待 DWM 下一次合成（近似垂直同步），用于给连续动画定速。不可用时立即返回。</summary>
    internal static void WaitForVBlank()
    {
        if (s_noDwmFlush) return;
        try
        {
            if (Dwmapi.DwmFlush() < 0) s_noDwmFlush = true; // 例如合成被禁用（Win7 Basic）
        }
        catch (DllNotFoundException) { s_noDwmFlush = true; }
        catch (EntryPointNotFoundException) { s_noDwmFlush = true; }
    }
}

/// <summary>
/// 主线程调度器：操作进入加锁队列，通过向仅消息窗口投递私有消息唤醒消息循环执行。
/// 每次只执行投递时已在队列中的操作，执行期间新投递的操作留到下一条消息，避免饿死输入与绘制。
/// </summary>
internal sealed class Win32Dispatcher : IDispatcher
{
    readonly IntPtr _hwnd;
    readonly Queue<Action> _queue = new();
    readonly int _threadId = Environment.CurrentManagedThreadId;
    bool _scheduled;

    public Win32Dispatcher(IntPtr messageWindow) => _hwnd = messageWindow;

    public bool CheckAccess() => Environment.CurrentManagedThreadId == _threadId;

    public void Post(Action action)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));
        bool schedule;
        lock (_queue)
        {
            _queue.Enqueue(action);
            schedule = !_scheduled;
            _scheduled = true;
        }
        if (schedule && User32.PostMessageW(_hwnd, Win32Platform.WM_AWAUI_DRAIN, IntPtr.Zero, IntPtr.Zero) == 0)
        {
            // 投递失败（消息队列已满）：允许下一次 Post 重试
            lock (_queue) _scheduled = false;
        }
    }

    public void Drain()
    {
        int count;
        lock (_queue) count = _queue.Count;
        try
        {
            for (int i = 0; i < count; i++)
            {
                Action a;
                lock (_queue) a = _queue.Dequeue();
                a();
            }
        }
        finally
        {
            // 即使某个操作抛出异常，也要保证剩余操作会被再次调度
            bool repost;
            lock (_queue)
            {
                repost = _queue.Count > 0;
                _scheduled = repost;
            }
            if (repost && User32.PostMessageW(_hwnd, Win32Platform.WM_AWAUI_DRAIN, IntPtr.Zero, IntPtr.Zero) == 0)
                lock (_queue) _scheduled = false;
        }
    }
}
