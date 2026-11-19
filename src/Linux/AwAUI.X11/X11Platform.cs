using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using AwAUI.Platform;
using AwAUI.Threading;
using AwAUI.X11.Interop;
using static AwAUI.X11.Interop.Xlib;

namespace AwAUI.X11;

/// <summary>
/// Linux X11 平台后端（design/04）：libX11 裸 P/Invoke，软件呈现（XPutImage，可用时走 MIT-SHM）。
/// 消息循环在创建平台的线程上运行：poll 同时等待 X 连接与自唤醒管道，<see cref="IDispatcher.Post"/> 可从任意线程唤醒它；
/// <see cref="WindowImpl.RequestFrame"/> 的帧按单调时钟限速（默认 60Hz）在下一轮循环中发出。
/// 只支持 64 位（LP64）进程。
/// </summary>
public sealed unsafe class X11Platform : PlatformBackend, IDisposable
{
    readonly X11Dispatcher _dispatcher;
    readonly Dictionary<ulong, X11Window> _windowsByHandle = new();
    readonly List<X11Window> _windows = new();
    readonly Dictionary<CursorShape, ulong> _cursors = new();
    readonly int _threadId = Environment.CurrentManagedThreadId;
    readonly int _connectionFd;
    readonly long _epoch;

    IntPtr _display;
    IntPtr _im;
    bool _imCallbacks;
    X11Clipboard? _clipboard;
    ulong _lastRawTime;
    long _lastFrame;
    volatile bool _exit;
    bool _running;
    bool _disposed;

    // X 服务器时间（32 位毫秒，约 49.7 天回绕）扩展为 64 位
    uint _lastServerTime;
    long _serverTimeWraps;

    /// <summary>
    /// 连接 DISPLAY 指定的 X 服务器。非 Linux、32 位进程、DISPLAY 未设置、libX11 不可用或无法连接时抛出
    /// <see cref="PlatformNotSupportedException"/>。
    /// </summary>
    public X11Platform()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            throw new PlatformNotSupportedException("X11Platform requires Linux.");
        if (IntPtr.Size != 8)
            throw new PlatformNotSupportedException("X11Platform supports 64-bit processes only (Xlib structure layouts are declared for LP64).");
        var displayName = Environment.GetEnvironmentVariable("DISPLAY");
        if (string.IsNullOrEmpty(displayName))
            throw new PlatformNotSupportedException("X11Platform: the DISPLAY environment variable is not set, so no X server is available.");

        try
        {
            XInitThreads(); // 必须是进程中第一个 Xlib 调用
            _display = XOpenDisplay(null);
        }
        catch (DllNotFoundException e)
        {
            throw new PlatformNotSupportedException("X11Platform: libX11.so.6 could not be loaded (is libX11 installed?).", e);
        }
        catch (EntryPointNotFoundException e)
        {
            throw new PlatformNotSupportedException("X11Platform: libX11.so.6 is missing required entry points.", e);
        }
        if (_display == IntPtr.Zero)
            throw new PlatformNotSupportedException($"X11Platform: cannot open X display '{displayName}'.");

        try
        {
            XErrors.Install();
            Screen = XDefaultScreen(_display);
            Root = XRootWindow(_display, Screen);
            Visual = X11Visual.Choose(_display, Screen, Root);
            Scale = ReadScale(_display);
            _connectionFd = XConnectionNumber(_display);

            WmProtocols = InternAtom(_display, "WM_PROTOCOLS");
            WmDeleteWindow = InternAtom(_display, "WM_DELETE_WINDOW");
            NetWmName = InternAtom(_display, "_NET_WM_NAME");
            NetWmIconName = InternAtom(_display, "_NET_WM_ICON_NAME");
            Utf8String = InternAtom(_display, "UTF8_STRING");

            int supported = 0;
            DetectableAutoRepeat = XkbSetDetectableAutoRepeat(_display, 1, &supported) != 0 && supported != 0;

            InitSharedMemory(displayName!);
            InitInputMethod();
            _dispatcher = new X11Dispatcher();
        }
        catch
        {
            XCloseDisplay(_display);
            _display = IntPtr.Zero;
            throw;
        }
        _epoch = LibC.MonotonicTicks();
    }

    public override string Name => "X11";

    /// <summary>系统剪贴板（CLIPBOARD 选择）。第一次访问时创建选择窗口；只能在平台线程上首次访问。</summary>
    public override Clipboard Clipboard => _clipboard ??= new X11Clipboard(this);

    /// <summary>最近一个输入事件的服务器时间（选择协议要求用真实时间戳而不是 CurrentTime）；还没有输入事件时为 0。</summary>
    internal ulong LastEventTime => _lastRawTime;

    /// <summary>输入法服务器是否支持 on-the-spot 预编辑（XIMPreeditCallbacks）。</summary>
    public bool SupportsPreeditCallbacks => _imCallbacks;
    public override IDispatcher Dispatcher => _dispatcher;
    public IReadOnlyList<X11Window> Windows => _windows;

    /// <summary>Display* 句柄。</summary>
    public IntPtr Display => _display;

    /// <summary>逻辑像素到设备像素的缩放：AWAUI_SCALE 环境变量，否则 Xft.dpi / 96，默认 1。</summary>
    public double Scale { get; }

    /// <summary>帧之间的最小间隔（默认 60Hz）。X11 后端尚无垂直同步（Present 扩展），以单调时钟限速。</summary>
    public TimeSpan FrameInterval { get; set; } = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);

    /// <summary>是否可以（仍然）使用 MIT-SHM 呈现。首次附加失败后关闭。</summary>
    public bool ShmAvailable { get; private set; }

    /// <summary>是否打开了 X 输入法（XIM）；否则文本经 XLookupString（Latin-1）获取。</summary>
    public bool HasInputMethod => _im != IntPtr.Zero;

    internal int Screen { get; }
    internal ulong Root { get; }
    internal X11Visual Visual { get; }
    internal bool DetectableAutoRepeat { get; }
    internal int ShmEventBase { get; private set; } = -1;
    internal ulong WmProtocols { get; }
    internal ulong WmDeleteWindow { get; }
    internal ulong NetWmName { get; }
    internal ulong NetWmIconName { get; }
    internal ulong Utf8String { get; }

    public override WindowImpl CreateWindow(WindowOptions options)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(X11Platform));
        _dispatcher.VerifyAccess();
        var w = new X11Window(this, options);
        _windows.Add(w);
        _windowsByHandle[w.Handle] = w;
        return w;
    }

    internal void OnWindowClosed(X11Window w)
    {
        _windowsByHandle.Remove(w.Handle);
        _windows.Remove(w);
        if (_windows.Count == 0) Exit();
    }

    // ---------------------------------------------------------------- 消息循环

    public override void Run()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(X11Platform));
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("X11Platform.Run must be called on the thread that created the platform.");
        if (_running) throw new InvalidOperationException("X11Platform.Run is already running.");
        _running = true;
        _exit = false;
        var fds = stackalloc PollFd[2];
        try
        {
            while (!_exit)
            {
                _clipboard?.CheckTimeouts(LibC.MonotonicTicks());
                _dispatcher.Drain();
                if (_exit) break;
                ProcessEvents();
                if (_exit) break;

                int timeout = -1;
                long interval = Math.Max(1, FrameInterval.Ticks);
                if (AnyFrameWanted())
                {
                    long now = LibC.MonotonicTicks();
                    long due = _lastFrame + interval;
                    if (now >= due)
                    {
                        // 落后不足一个间隔时沿节拍推进，避免累积漂移；否则从现在重新起拍
                        _lastFrame = now - due < interval ? due : now;
                        DeliverFrames(now);
                        if (_exit) break;
                    }
                    if (AnyFrameWanted())
                    {
                        now = LibC.MonotonicTicks();
                        long wait = _lastFrame + interval - now;
                        timeout = wait <= 0 ? 0 : (int)Math.Min(int.MaxValue, (wait + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond);
                    }
                }
                if (_dispatcher.HasWork) timeout = 0;
                if (_clipboard?.NextDeadline is long deadline)
                {
                    long wait = deadline - LibC.MonotonicTicks();
                    int ms = wait <= 0 ? 0 : (int)Math.Min(int.MaxValue, (wait + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond);
                    timeout = timeout < 0 ? ms : Math.Min(timeout, ms);
                }

                // XPending 先冲刷输出缓冲（包括刚发出的 XPutImage），并读入已到达的事件；有事件就不睡眠
                if (XPending(_display) > 0) continue;
                if (timeout == 0) continue;

                fds[0] = new PollFd { fd = _connectionFd, events = LibC.POLLIN };
                fds[1] = new PollFd { fd = _dispatcher.WakeFd, events = LibC.POLLIN };
                int n = LibC.poll(fds, 2, timeout);
                if (n > 0 && (fds[1].revents & LibC.POLLIN) != 0) _dispatcher.ClearWake();
                // n < 0（EINTR 等）直接进入下一轮
            }
        }
        finally
        {
            _running = false;
        }
    }

    public override void Exit()
    {
        if (!_running) return;
        _exit = true;
        _dispatcher.Wake();
    }

    void ProcessEvents()
    {
        XEvent ev;
        while (!_exit && _display != IntPtr.Zero && XPending(_display) > 0)
        {
            XNextEvent(_display, &ev);
            // 输入法可能吞掉事件（组合键、候选选择）；所有事件都要先交给它
            if (XFilterEvent(&ev, 0) != 0) continue;
            Dispatch(&ev);
        }
    }

    void Dispatch(XEvent* ev)
    {
        if (ev->type == MappingNotify)
        {
            XRefreshKeyboardMapping(ev); // 键盘布局变化：更新 XLookupString 使用的映射
            return;
        }
        if (_clipboard is not null && _clipboard.HandleEvent(ev)) return;
        if (!_windowsByHandle.TryGetValue(ev->xany.window, out var w)) return;
        if (ShmEventBase >= 0 && ev->type == ShmEventBase + XShm.ShmCompletion)
        {
            w.OnShmCompletion(); // XShmCompletionEvent.drawable 与 XAnyEvent.window 同在偏移 32
            return;
        }
        w.HandleEvent(ev);
    }

    bool AnyFrameWanted()
    {
        foreach (var w in _windows)
            if (w.WantsFrame) return true;
        return false;
    }

    void DeliverFrames(long now)
    {
        var t = TimeSpan.FromTicks(now - _epoch);
        foreach (var w in _windows.ToArray())
        {
            if (_exit) return;
            if (w.WantsFrame) w.DeliverFrame(t);
        }
    }

    /// <summary>X 服务器时间（毫秒）→ 单调的 64 位时间戳。</summary>
    internal TimeSpan ServerTime(ulong time)
    {
        uint t = (uint)time;
        if (time != 0) _lastRawTime = time;
        if (t < _lastServerTime && _lastServerTime - t > 0x80000000u) _serverTimeWraps++;
        _lastServerTime = t;
        return TimeSpan.FromTicks(((_serverTimeWraps << 32) + t) * TimeSpan.TicksPerMillisecond);
    }

    // ---------------------------------------------------------------- MIT-SHM

    void InitSharedMemory(string displayName)
    {
        if (Environment.GetEnvironmentVariable("AWAUI_X11_SHM") == "0") return;
        if (Visual.DirectFormat == null) return; // 转换形态不能零拷贝
        // "host:0" / "localhost:10"（SSH 转发）是 TCP 连接，服务器看不到本机共享内存；":0" 与 "unix:0" 是本地
        int colon = displayName.LastIndexOf(':');
        string host = colon > 0 ? displayName.Substring(0, colon) : "";
        if (host.Length > 0 && host != "unix" && !host.StartsWith("/", StringComparison.Ordinal)) return;
        try
        {
            if (XShm.XShmQueryExtension(_display) == 0) return;
            if (XImageByteOrder(_display) != LSBFirst) return; // 共享内存不经 Xlib 交换字节序
            ShmEventBase = XShm.XShmGetEventBase(_display);
            ShmAvailable = true;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    internal void DisableShm(string reason)
    {
        if (!ShmAvailable) return;
        ShmAvailable = false;
        if (Environment.GetEnvironmentVariable("AWAUI_X11_DEBUG") == "1")
            Console.Error.WriteLine("[AwAUI.X11] MIT-SHM disabled, falling back to XPutImage: " + reason);
    }

    // ---------------------------------------------------------------- 输入法

    void InitInputMethod()
    {
        if (Environment.GetEnvironmentVariable("AWAUI_X11_XIM") == "0") return;
        try
        {
            // XIM 依赖 C 库的 LC_CTYPE 区域设置（与 GTK/Qt 相同的做法）
            var empty = stackalloc byte[1];
            empty[0] = 0;
            LibC.setlocale(LibC.LC_CTYPE, empty);
            if (XSupportsLocale() == 0) return;
            XSetLocaleModifiers(empty);
            _im = XOpenIM(_display, IntPtr.Zero, null, null);
            if (_im == IntPtr.Zero)
            {
                // 没有输入法服务器：退回 Xlib 内建的组合键处理（仍可用 Xutf8LookupString）
                var none = Z("@im=none");
                fixed (byte* p = none) XSetLocaleModifiers(p);
                _im = XOpenIM(_display, IntPtr.Zero, null, null);
            }
        }
        catch (DllNotFoundException) { _im = IntPtr.Zero; }
        catch (EntryPointNotFoundException) { _im = IntPtr.Zero; }
        if (_im == IntPtr.Zero) return;
        _imCallbacks = Environment.GetEnvironmentVariable("AWAUI_X11_PREEDIT") != "root" && QueryStyle(XIMPreeditCallbacks | XIMStatusNothing);
        // 预编辑回调的客户数据是窗口 XID
        Callbacks.OnPreedit = (kind, client, call) =>
        {
            if (_windowsByHandle.TryGetValue((ulong)(long)client, out var w)) w.OnPreedit(kind, call);
        };
    }

    static readonly byte[] s_xnQueryInputStyle = Z("queryInputStyle");

    /// <summary>输入法服务器是否支持给定的输入风格。</summary>
    bool QueryStyle(long style)
    {
        IntPtr stylesPtr = IntPtr.Zero;
        fixed (byte* name = s_xnQueryInputStyle)
            if (XGetIMValues(_im, name, &stylesPtr, IntPtr.Zero) != IntPtr.Zero || stylesPtr == IntPtr.Zero) return false;
        try
        {
            var styles = (XIMStyles*)stylesPtr;
            int n = Math.Min((int)styles->count_styles, 256);
            for (int i = 0; i < n; i++)
                if ((long)styles->supported_styles[i] == style) return true;
            return false;
        }
        finally
        {
            XFree((void*)stylesPtr);
        }
    }

    static readonly byte[] s_xnInputStyle = Z("inputStyle");
    static readonly byte[] s_xnClientWindow = Z("clientWindow");
    static readonly byte[] s_xnFocusWindow = Z("focusWindow");

    static readonly byte[] s_xnPreeditAttributes = Z("preeditAttributes");
    static readonly byte[] s_xnPreeditStart = Z("preeditStartCallback");
    static readonly byte[] s_xnPreeditDone = Z("preeditDoneCallback");
    static readonly byte[] s_xnPreeditDraw = Z("preeditDrawCallback");
    static readonly byte[] s_xnPreeditCaret = Z("preeditCaretCallback");

    /// <summary>
    /// 为窗口创建输入上下文。服务器支持时用 on-the-spot（XIMPreeditCallbacks：预编辑经回调交给框架绘制，回调结构放在
    /// <paramref name="callbacks"/> 指向的非托管内存中，随窗口释放），否则用 Root 风格（预编辑与状态由输入法自己显示）。
    /// 失败时返回 0，窗口退回 XLookupString。
    /// </summary>
    internal IntPtr CreateInputContext(ulong window, out IntPtr callbacks, out bool onTheSpot)
    {
        callbacks = IntPtr.Zero;
        onTheSpot = false;
        if (_im == IntPtr.Zero) return IntPtr.Zero;
        fixed (byte* style = s_xnInputStyle)
        fixed (byte* client = s_xnClientWindow)
        fixed (byte* focus = s_xnFocusWindow)
        {
            if (_imCallbacks)
            {
                var mem = Marshal.AllocHGlobal(sizeof(XIMCallback) * 4);
                var cb = (XIMCallback*)mem;
                var data = (IntPtr)(long)window;
                cb[0] = new XIMCallback { client_data = data, callback = Callbacks.PreeditStart };
                cb[1] = new XIMCallback { client_data = data, callback = Callbacks.PreeditDone };
                cb[2] = new XIMCallback { client_data = data, callback = Callbacks.PreeditDraw };
                cb[3] = new XIMCallback { client_data = data, callback = Callbacks.PreeditCaret };
                IntPtr ic = IntPtr.Zero;
                fixed (byte* attrs = s_xnPreeditAttributes)
                fixed (byte* start = s_xnPreeditStart)
                fixed (byte* done = s_xnPreeditDone)
                fixed (byte* draw = s_xnPreeditDraw)
                fixed (byte* caret = s_xnPreeditCaret)
                {
                    var list = XVaCreateNestedList4(0, start, (IntPtr)(&cb[0]), done, (IntPtr)(&cb[1]), draw, (IntPtr)(&cb[2]), caret, (IntPtr)(&cb[3]), IntPtr.Zero);
                    if (list != IntPtr.Zero)
                    {
                        ic = XCreateIC4(_im, style, (IntPtr)(XIMPreeditCallbacks | XIMStatusNothing), client, (IntPtr)(long)window,
                            focus, (IntPtr)(long)window, attrs, list, IntPtr.Zero);
                        XFree((void*)list);
                    }
                }
                if (ic != IntPtr.Zero)
                {
                    callbacks = mem;
                    onTheSpot = true;
                    return ic;
                }
                Marshal.FreeHGlobal(mem);
            }
            return XCreateIC(_im, style, (IntPtr)(XIMPreeditNothing | XIMStatusNothing),
                client, (IntPtr)(long)window, focus, (IntPtr)(long)window, IntPtr.Zero);
        }
    }

    // ---------------------------------------------------------------- 光标

    internal ulong GetCursor(CursorShape shape)
    {
        if (_cursors.TryGetValue(shape, out var c)) return c;
        if (shape == CursorShape.None)
        {
            // 1x1 全透明位图光标
            byte zero = 0;
            ulong pixmap = XCreateBitmapFromData(_display, Root, &zero, 1, 1);
            XColor black = default;
            c = XCreatePixmapCursor(_display, pixmap, pixmap, &black, &black, 0, 0);
            XFreePixmap(_display, pixmap);
        }
        else
        {
            uint glyph = shape switch
            {
                CursorShape.IBeam => XC_xterm,
                CursorShape.Hand => XC_hand2,
                CursorShape.Crosshair => XC_crosshair,
                CursorShape.ResizeHorizontal => XC_sb_h_double_arrow,
                CursorShape.ResizeVertical => XC_sb_v_double_arrow,
                CursorShape.Wait => XC_watch,
                CursorShape.NotAllowed => XC_X_cursor,
                _ => XC_left_ptr,
            };
            c = XCreateFontCursor(_display, glyph);
        }
        _cursors[shape] = c;
        return c;
    }

    // ---------------------------------------------------------------- DPI

    static double ReadScale(IntPtr display)
    {
        var env = Environment.GetEnvironmentVariable("AWAUI_SCALE");
        if (!string.IsNullOrEmpty(env) && double.TryParse(env, NumberStyles.Float, CultureInfo.InvariantCulture, out var forced) && forced > 0)
            return forced;
        var rms = (byte*)XResourceManagerString(display);
        if (rms != null)
        {
            double? dpi = ParseXftDpi(ReadLatin1(rms));
            if (dpi is double d && d > 0) return d / 96.0;
        }
        return 1.0;
    }

    /// <summary>在 X 资源字符串（"name:\tvalue" 每行一条）中查找 Xft.dpi。</summary>
    internal static double? ParseXftDpi(string resources)
    {
        foreach (var raw in resources.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("Xft.dpi", StringComparison.Ordinal)) continue;
            int colon = line.IndexOf(':');
            if (colon < 0 || line.Substring(0, colon).Trim() != "Xft.dpi") continue;
            if (double.TryParse(line.Substring(colon + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dpi))
                return dpi;
        }
        return null;
    }

    internal static string ReadLatin1(byte* s)
    {
        var sb = new StringBuilder();
        for (; *s != 0; s++) sb.Append((char)*s);
        return sb.ToString();
    }

    // ---------------------------------------------------------------- 释放

    /// <summary>关闭所有窗口与 X 连接。之后平台不可再用。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        foreach (var w in _windows.ToArray()) w.Close();
        _disposed = true;
        if (_display != IntPtr.Zero)
        {
            foreach (var c in _cursors.Values) XFreeCursor(_display, c);
            _cursors.Clear();
            _clipboard?.Dispose();
            _clipboard = null;
            if (_im != IntPtr.Zero) { XCloseIM(_im); _im = IntPtr.Zero; }
            Visual.Free(_display);
            XCloseDisplay(_display);
            _display = IntPtr.Zero;
        }
        _dispatcher.Dispose();
    }
}

/// <summary>
/// 主线程调度器：队列 + 自唤醒管道。<see cref="Post"/> 可从任意线程调用，向管道写一个字节唤醒 poll；
/// 同一轮内多次投递只写一次。
/// </summary>
internal sealed unsafe class X11Dispatcher : IDispatcher, IDisposable
{
    readonly Queue<Action> _queue = new();
    readonly int _threadId = Environment.CurrentManagedThreadId;
    readonly int _readFd;
    readonly int _writeFd;
    bool _signaled;

    public X11Dispatcher()
    {
        int* fds = stackalloc int[2];
        if (LibC.pipe2(fds, LibC.O_NONBLOCK | LibC.O_CLOEXEC) != 0)
            throw new InvalidOperationException($"pipe2 failed (errno {Marshal.GetLastWin32Error()}).");
        _readFd = fds[0];
        _writeFd = fds[1];
    }

    public int WakeFd => _readFd;

    public bool CheckAccess() => Environment.CurrentManagedThreadId == _threadId;

    public void Post(Action action)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
        bool wake;
        lock (_queue)
        {
            _queue.Enqueue(action);
            wake = !_signaled;
            _signaled = true;
        }
        if (wake) Wake();
    }

    public bool HasWork { get { lock (_queue) return _queue.Count > 0; } }

    /// <summary>向管道写一个字节；管道已满（EAGAIN）说明循环必然会醒来，忽略即可。</summary>
    public void Wake()
    {
        byte b = 1;
        LibC.write(_writeFd, &b, (UIntPtr)1u);
    }

    /// <summary>读空管道（非阻塞）。</summary>
    public void ClearWake()
    {
        var buffer = stackalloc byte[64];
        while ((long)LibC.read(_readFd, buffer, (UIntPtr)64u) > 0) { }
    }

    /// <summary>执行所有排队的操作（包括执行期间新投递的）。</summary>
    public void Drain()
    {
        while (true)
        {
            Action a;
            lock (_queue)
            {
                if (_queue.Count == 0) { _signaled = false; return; }
                a = _queue.Dequeue();
            }
            a();
        }
    }

    public void Dispose()
    {
        LibC.close(_readFd);
        LibC.close(_writeFd);
    }
}
