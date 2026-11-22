using System;
using System.Collections.Generic;
using System.Threading;
using AwAUI.Graphics;
using AwAUI.Platform;
using AwAUI.Threading;

namespace AwAUI.Headless;

/// <summary>
/// 无窗口平台：窗口的帧缓冲就是内存位图。用于测试、截图与服务端渲染。
/// 时间由调用方推进（<see cref="RunFrame"/>），结果完全确定。
/// 同时提供可脚本化的显示器、文件选择器、外观启动器与内存剪贴板，作为第十二批平台 API 的参考实现与测试床。
/// </summary>
public sealed class HeadlessPlatform : PlatformBackend
{
    readonly HeadlessDispatcher _dispatcher = new();
    readonly List<HeadlessWindow> _windows = new();
    readonly HeadlessClipboard _clipboard = new();
    readonly HeadlessScreens _screens = new();
    readonly HeadlessStorageProvider _storage = new();
    readonly HeadlessLauncher _launcher = new();
    bool _exit;
    TimeSpan _time;

    public override string Name => "Headless";
    public override IDispatcher Dispatcher => _dispatcher;

    /// <summary>每帧推进的时间（默认 60Hz）。</summary>
    public TimeSpan FrameInterval { get; set; } = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60);

    public IReadOnlyList<HeadlessWindow> Windows => _windows;

    public double DefaultScale { get; set; } = 1.0;

    /// <summary>新窗口报告的滚动条样式偏好（见 <see cref="HeadlessWindow.ScrollbarStyle"/>）。</summary>
    public ScrollbarStyle DefaultScrollbarStyle { get; set; } = ScrollbarStyle.Overlay;

    /// <summary>新窗口的键盘布局（<see cref="HeadlessWindow.TypeKey"/> 用它求逻辑键与文本）。</summary>
    public HeadlessKeyboardLayout DefaultKeyboardLayout { get; set; } = HeadlessKeyboardLayout.UnitedStates;

    /// <summary>内存中的剪贴板（所有窗口共享）。</summary>
    public override Clipboard Clipboard => _clipboard;

    /// <inheritdoc cref="Clipboard"/>
    public HeadlessClipboard HeadlessClipboard => _clipboard;

    /// <summary>可脚本化的显示器集合（所有窗口共享）；默认一块 1920×1080 主显示器。</summary>
    public override Screens Screens => _screens;

    /// <inheritdoc cref="Screens"/>
    public HeadlessScreens HeadlessScreens => _screens;

    /// <summary>可脚本化的文件与目录选择器（所有窗口共享）。</summary>
    public override StorageProvider StorageProvider => _storage;

    /// <inheritdoc cref="StorageProvider"/>
    public HeadlessStorageProvider HeadlessStorageProvider => _storage;

    /// <summary>可脚本化的默认程序启动器（所有窗口共享）。</summary>
    public override Launcher Launcher => _launcher;

    /// <inheritdoc cref="Launcher"/>
    public HeadlessLauncher HeadlessLauncher => _launcher;

    public override T? GetService<T>() where T : class
    {
        if (_clipboard is T clipboard) return clipboard;
        if (_screens is T screens) return screens;
        if (_storage is T storage) return storage;
        if (_launcher is T launcher) return launcher;
        return null;
    }

    public override WindowImpl CreateWindow(WindowOptions options)
    {
        var w = new HeadlessWindow(this, options ?? new WindowOptions(), DefaultScale) { KeyboardLayout = DefaultKeyboardLayout };
        _windows.Add(w);
        return w;
    }

    internal void Remove(HeadlessWindow w) => _windows.Remove(w);

    /// <summary>执行排队的操作，并为请求了帧的窗口推进一帧。</summary>
    public void RunFrame()
    {
        _dispatcher.Drain();
        _time += FrameInterval;
        foreach (var w in _windows.ToArray()) w.DeliverFrame(_time);
        _dispatcher.Drain();
    }

    public override void Run()
    {
        _exit = false;
        while (!_exit && _windows.Count > 0)
        {
            RunFrame();
            bool pending = false;
            foreach (var w in _windows) pending |= w.FrameRequested;
            if (!pending && !_dispatcher.HasWork) _dispatcher.WaitForWork(TimeSpan.FromMilliseconds(16));
        }
    }

    public override void Exit() => _exit = true;
}

internal sealed class HeadlessDispatcher : IDispatcher
{
    readonly Queue<Action> _queue = new();
    readonly object _lock = new();
    readonly int _threadId = Environment.CurrentManagedThreadId;

    public bool CheckAccess() => Environment.CurrentManagedThreadId == _threadId;

    public void Post(Action action)
    {
        lock (_lock)
        {
            _queue.Enqueue(action);
            Monitor.PulseAll(_lock);
        }
    }

    public bool HasWork { get { lock (_lock) return _queue.Count > 0; } }

    public void WaitForWork(TimeSpan timeout)
    {
        lock (_lock)
            if (_queue.Count == 0) Monitor.Wait(_lock, timeout);
    }

    public void Drain()
    {
        while (true)
        {
            Action a;
            lock (_lock)
            {
                if (_queue.Count == 0) return;
                a = _queue.Dequeue();
            }
            a();
        }
    }
}

/// <summary>
/// 无窗口窗口：把 <see cref="WindowOptions"/> 中的新字段（位置、启动位置、状态、置顶、尺寸约束、图标、
/// 任务栏可见性、<see cref="SizeToContent"/>）记录为可断言的快照，并实现位置 / 状态 / 激活 / 重绘等平台成员。
/// <para>
/// 平台回调由测试脚本驱动：<see cref="SendStateChanged"/>、<see cref="SendPositionChanged"/>、
/// <see cref="SendActivated"/>、<see cref="SendLifecycle"/>、<see cref="SendSafeArea"/> 与
/// <see cref="SendSurfaceLost"/> / <see cref="SendSurfaceRestored"/> 只调用 <see cref="WindowImpl.Events"/> 上对应的可选接口，
/// 与真实平台一致。
/// </para>
/// </summary>
public sealed class HeadlessWindow : WindowImpl, IWindowChrome
{
    readonly HeadlessPlatform _platform;
    readonly HeadlessSurface _surface;
    Size _size;
    double _scale;
    Point _position;
    WindowState _state;
    bool _topmost;
    bool _active = true;
    Thickness _safeArea;
    int _invalidateCount;
    Rect _lastInvalidatedRect;

    internal HeadlessWindow(HeadlessPlatform platform, WindowOptions options, double scale)
    {
        _platform = platform;
        Options = Clone(options);
        _scale = scale;
        _size = ApplySizeConstraints(Options.Size, Options);
        _position = ResolvePosition(Options, platform, _size);
        _state = Options.State;
        _topmost = Options.Topmost;
        Decorations = Options.Decorations;
        _surface = new HeadlessSurface(this);
        _textInput = new HeadlessTextInput(this);
        ScrollbarStyle = platform.DefaultScrollbarStyle;
    }

    /// <summary>创建时应用后的选项快照（之后的修改不会反映到这里）。</summary>
    public WindowOptions Options { get; }

    /// <summary>创建时请求的窗口装饰方式（供测试断言）。</summary>
    public WindowDecorations Decorations { get; }

    /// <summary>创建时请求的启动位置策略。</summary>
    public WindowStartupLocation StartupLocation => Options.StartupLocation;

    /// <summary>创建时请求的最小 / 最大尺寸与图标等（便捷访问 <see cref="Options"/>）。</summary>
    public double? MinWidth => Options.MinWidth;
    public double? MinHeight => Options.MinHeight;
    public double? MaxWidth => Options.MaxWidth;
    public double? MaxHeight => Options.MaxHeight;
    public Bitmap? Icon => Options.Icon;
    public bool ShowInTaskbar => Options.ShowInTaskbar;

    /// <summary>拖动请求次数（<see cref="BeginDragMove"/>）。</summary>
    public int DragMoveCount { get; private set; }

    /// <summary>是否被最小化（由 <see cref="WindowState"/> 推导）。</summary>
    public bool IsMinimized => _state == AwAUI.Platform.WindowState.Minimized;

    /// <summary>是否最大化或全屏（由 <see cref="WindowState"/> 推导）。</summary>
    public bool IsMaximized =>
        _state == AwAUI.Platform.WindowState.Maximized || _state == AwAUI.Platform.WindowState.FullScreen;

    public void BeginDragMove() => DragMoveCount++;

    public void Minimize() => _state = AwAUI.Platform.WindowState.Minimized;

    public void ToggleMaximize() =>
        _state = IsMaximized ? AwAUI.Platform.WindowState.Normal : AwAUI.Platform.WindowState.Maximized;

    readonly HeadlessTextInput _textInput;

    /// <summary>可由测试脚本驱动的输入法桥。</summary>
    public override TextInputMethod TextInput => _textInput;

    /// <inheritdoc cref="TextInput"/>
    public HeadlessTextInput HeadlessTextInput => _textInput;

    public override Clipboard Clipboard => _platform.Clipboard;

    public override Screens Screens => _platform.Screens;

    public override StorageProvider StorageProvider => _platform.StorageProvider;

    public override Launcher Launcher => _platform.Launcher;

    /// <summary><see cref="TypeKey"/> 使用的键盘布局。</summary>
    public HeadlessKeyboardLayout KeyboardLayout { get; set; } = HeadlessKeyboardLayout.UnitedStates;

    /// <summary>窗口已关闭。</summary>
    public bool IsClosed { get; private set; }

    public override NativeSurface Surface => _surface;
    public override Size ClientSize => _size;

    /// <summary>窗口报告的滚动条样式偏好（宿主创建时读取；默认覆盖式）。初值取 <see cref="HeadlessPlatform.DefaultScrollbarStyle"/>。</summary>
    public ScrollbarStyle ScrollbarStyle { get; set; }

    public override ScrollbarStyle PreferredScrollbarStyle => ScrollbarStyle;
    public override double RenderScale => _scale;
    public string Title { get; private set; } = "";
    public bool IsVisible { get; private set; }
    internal bool FrameRequested { get; private set; }

    /// <summary>最近一次呈现的帧。</summary>
    public Bitmap? Framebuffer => _surface.Bitmap;

    /// <summary>最近一次设置的光标形状（供测试检查）。</summary>
    public CursorShape Cursor { get; private set; } = CursorShape.Arrow;

    // ------------------------------------------------------------ 位置 / 状态 / 激活

    /// <summary>窗口左上角在屏幕逻辑坐标中的位置。程序调用 <see cref="SetPosition"/> 立即更新。</summary>
    public override Point Position => _position;

    public override void SetPosition(Point position) => _position = position;

    public override WindowState WindowState => _state;

    public override void SetWindowState(WindowState state) => _state = state;

    public override bool Topmost
    {
        get => _topmost;
        set => _topmost = value;
    }

    public override bool IsActive => _active;

    /// <summary>请求激活：headless 中立即把 <see cref="IsActive"/> 设为 true，但不回调事件（用 <see cref="SendActivated"/> 模拟平台报告）。</summary>
    public override void Activate() => _active = true;

    public override Thickness SafeAreaInsets => _safeArea;

    /// <summary><see cref="Invalidate"/> 被调用的次数。</summary>
    public int InvalidateCount => _invalidateCount;

    /// <summary>最近一次 <see cref="Invalidate"/> 的矩形（空矩形表示整窗）。</summary>
    public Rect LastInvalidatedRect => _lastInvalidatedRect;

    /// <summary>最近一次 <see cref="Invalidate"/> 是否请求了整个窗口。</summary>
    public bool LastInvalidationWasWholeWindow => _lastInvalidatedRect.IsEmpty;

    /// <summary>请求重绘指定区域（逻辑像素；空矩形表示整窗），并像平台一样请求下一帧。</summary>
    public override void Invalidate(in Rect rect)
    {
        _invalidateCount++;
        _lastInvalidatedRect = rect;
        RequestFrame();
    }

    public override void Show() => IsVisible = true;
    public override void SetTitle(string title) => Title = title;
    public override void SetCursor(CursorShape cursor) => Cursor = cursor;
    public override void RequestFrame() => FrameRequested = true;

    public override void Close()
    {
        if (IsClosed) return;
        if (Events?.OnCloseRequested() == false) return;
        IsClosed = true;
        _platform.Remove(this);
        Events?.OnClosed();
        _surface.Dispose();
    }

    /// <summary>模拟窗口尺寸或缩放变化；尺寸按 <see cref="WindowOptions.MinWidth"/> / Max 约束。</summary>
    public void Resize(Size size, double scale)
    {
        _size = ApplySizeConstraints(size, Options);
        _scale = scale;
        Events?.OnResized(_size, scale);
    }

    // ------------------------------------------------------------ 可脚本化的平台回调

    /// <summary>模拟平台报告窗口状态变化（同时更新 <see cref="WindowState"/>）。</summary>
    public void SendStateChanged(WindowState state)
    {
        _state = state;
        (Events as IWindowStateEvents)?.OnWindowStateChanged(state);
    }

    /// <summary>模拟平台报告窗口位置变化（同时更新 <see cref="Position"/>）。</summary>
    public void SendPositionChanged(Point position)
    {
        _position = position;
        (Events as IWindowStateEvents)?.OnPositionChanged(position);
    }

    /// <summary>模拟平台报告窗口激活状态变化（同时更新 <see cref="IsActive"/>）。</summary>
    public void SendActivated(bool activated)
    {
        _active = activated;
        (Events as IWindowLifecycleEvents)?.OnActivated(activated);
    }

    /// <summary>模拟平台报告应用前后台状态变化。</summary>
    public void SendLifecycle(AppLifecycle state) =>
        (Events as IWindowLifecycleEvents)?.OnLifecycle(state);

    /// <summary>模拟平台报告安全区变化（同时更新 <see cref="SafeAreaInsets"/>）。</summary>
    public void SendSafeArea(Thickness insets)
    {
        _safeArea = insets;
        (Events as IWindowLifecycleEvents)?.OnSafeAreaChanged(insets);
    }

    /// <summary>模拟图形表面丢失（移动端切后台、GPU 设备丢失）。</summary>
    public void SendSurfaceLost() => (Events as IWindowLifecycleEvents)?.OnSurfaceLost();

    /// <summary>模拟图形表面恢复。</summary>
    public void SendSurfaceRestored() => (Events as IWindowLifecycleEvents)?.OnSurfaceRestored();

    public void SendPointer(in RawPointerEvent e) => Events?.OnPointer(e);
    public void SendKey(in RawKeyEvent e) => Events?.OnKey(e);

    /// <summary>
    /// 模拟可脚本化的滚轮输入：普通分格滚轮转动 <paramref name="down"/> 格（正值向下滚动，可为小数）与 <paramref name="right"/> 格（正值向右），
    /// 按 <paramref name="linesPerNotch"/> 行（Windows 默认 3 行）与 <see cref="WheelDeltas.PixelsPerLine"/> 换算成像素，与 Win32 / X11 后端一致。
    /// </summary>
    public void WheelNotches(Point position, float down, float right = 0, KeyModifiers modifiers = KeyModifiers.None,
        int linesPerNotch = WheelDeltas.DefaultLinesPerNotch, TimeSpan timestamp = default) =>
        Events?.OnPointer(RawPointerEvent.CreateWheel(position, -WheelDeltas.NotchesToPixels(right, linesPerNotch),
            -WheelDeltas.NotchesToPixels(down, linesPerNotch), WheelDeltaMode.Line, precise: false, ScrollPhase.None, ScrollPhase.None,
            PointerButtons.None, modifiers, timestamp));

    /// <summary>
    /// 模拟触控板（精确）滚动的一个事件：<paramref name="down"/> / <paramref name="right"/> 为向下 / 向右滚动的像素（与 macOS 的
    /// scrollingDeltaY 符号相反），带手势相位与系统惯性相位。
    /// </summary>
    public void TrackpadScroll(Point position, float down, float right, ScrollPhase phase, ScrollPhase momentumPhase = ScrollPhase.None,
        KeyModifiers modifiers = KeyModifiers.None, TimeSpan timestamp = default) =>
        Events?.OnPointer(RawPointerEvent.CreateWheel(position, -right, -down, WheelDeltaMode.Pixel, precise: true, phase, momentumPhase,
            PointerButtons.None, modifiers, timestamp));

    /// <summary>直接经 <see cref="IWindowEvents.OnTextInput"/> 送达文本（绕过输入法桥；平台式的送达见 <see cref="TypeText"/>）。</summary>
    public void SendText(string text) => Events?.OnTextInput(text);

    /// <summary>
    /// 像真实平台一样敲一个键：按 <see cref="KeyboardLayout"/> 求出逻辑键，发出按下；若该键产生文本（不是快捷键、控制键或死键），
    /// 把文本交给输入法桥的客户端（没有客户端时经 <see cref="IWindowEvents.OnTextInput"/>）；最后发出抬起。
    /// </summary>
    public void TypeKey(PhysicalKey key, KeyModifiers modifiers = KeyModifiers.None)
    {
        KeyboardLayout.Translate(key, modifiers, out var logical, out var text);
        Events?.OnKey(new RawKeyEvent(RawKeyEventType.Down, key, modifiers, false, 0, TimeSpan.Zero, logical));
        if (text is not null && !IsClosed) _textInput.DeliverText(text);
        if (!IsClosed) Events?.OnKey(new RawKeyEvent(RawKeyEventType.Up, key, modifiers, false, 0, TimeSpan.Zero, logical));
    }

    /// <summary>平台式地送达已提交的文本（有输入法客户端时交给客户端，否则经 <see cref="IWindowEvents.OnTextInput"/>），不产生按键事件。</summary>
    public void TypeText(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (text.Length > 0) _textInput.DeliverText(text);
    }

    internal void DeliverFrame(TimeSpan time)
    {
        if (!FrameRequested) return;
        FrameRequested = false;
        Events?.OnFrame(time);
    }

    // ------------------------------------------------------------ WindowOptions 应用

    static WindowOptions Clone(WindowOptions options) => new()
    {
        Title = options.Title,
        Size = options.Size,
        Resizable = options.Resizable,
        Position = options.Position,
        StartupLocation = options.StartupLocation,
        State = options.State,
        Topmost = options.Topmost,
        MinWidth = options.MinWidth,
        MinHeight = options.MinHeight,
        MaxWidth = options.MaxWidth,
        MaxHeight = options.MaxHeight,
        Icon = options.Icon,
        ShowInTaskbar = options.ShowInTaskbar,
        SizeToContent = options.SizeToContent,
        Decorations = options.Decorations,
    };

    static Size ApplySizeConstraints(Size size, WindowOptions options)
    {
        double width = size.Width, height = size.Height;
        if (IsFinite(options.MinWidth)) width = Math.Max(width, options.MinWidth!.Value);
        if (IsFinite(options.MinHeight)) height = Math.Max(height, options.MinHeight!.Value);
        if (IsFinite(options.MaxWidth)) width = Math.Min(width, options.MaxWidth!.Value);
        if (IsFinite(options.MaxHeight)) height = Math.Min(height, options.MaxHeight!.Value);
        return new Size((float)width, (float)height);
    }

    static bool IsFinite(double? value) =>
        value is { } v && !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0;

    static Point ResolvePosition(WindowOptions options, HeadlessPlatform platform, Size size)
    {
        if (options.StartupLocation == WindowStartupLocation.Manual)
            return options.Position ?? default;
        // CenterOwner 没有所有者概念：与 CenterScreen 一样在主显示器工作区居中。
        var area = platform.Screens.Primary.WorkingArea;
        return new Point(area.X + (area.Width - size.Width) / 2f, area.Y + (area.Height - size.Height) / 2f);
    }

    sealed class HeadlessSurface : NativeSurface, IFramebufferSurface, IDisposable
    {
        readonly HeadlessWindow _owner;
        public Bitmap? Bitmap;

        public HeadlessSurface(HeadlessWindow owner) => _owner = owner;

        public override PixelSize Size => PixelSize.FromSize(_owner._size, _owner._scale);
        public override double Scale => _owner._scale;

        public ILockedFramebuffer Lock()
        {
            var size = Size;
            if (Bitmap == null || Bitmap.Size != size)
            {
                Bitmap?.Dispose();
                Bitmap = new Bitmap(size);
            }
            return new Locked(Bitmap);
        }

        public void Dispose() => Bitmap?.Dispose();

        sealed class Locked : ILockedFramebuffer
        {
            readonly Bitmap _bitmap;
            public Locked(Bitmap bitmap) => _bitmap = bitmap;
            public IntPtr Address => _bitmap.Address;
            public PixelSize Size => _bitmap.Size;
            public int RowBytes => _bitmap.RowBytes;
            public PixelFormat Format => _bitmap.Format;
            public void Dispose() { }
        }
    }
}
