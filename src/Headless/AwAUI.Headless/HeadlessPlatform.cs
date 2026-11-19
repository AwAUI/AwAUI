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
/// </summary>
public sealed class HeadlessPlatform : PlatformBackend
{
    readonly HeadlessDispatcher _dispatcher = new();
    readonly List<HeadlessWindow> _windows = new();
    readonly HeadlessClipboard _clipboard = new();
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

    public override T? GetService<T>() where T : class => _clipboard as T;

    public override WindowImpl CreateWindow(WindowOptions options)
    {
        var w = new HeadlessWindow(this, options.Size, DefaultScale) { KeyboardLayout = DefaultKeyboardLayout };
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

public sealed class HeadlessWindow : WindowImpl
{
    readonly HeadlessPlatform _platform;
    readonly HeadlessSurface _surface;
    Size _size;
    double _scale;

    internal HeadlessWindow(HeadlessPlatform platform, Size size, double scale)
    {
        _platform = platform;
        _size = size;
        _scale = scale;
        _surface = new HeadlessSurface(this);
        _textInput = new HeadlessTextInput(this);
        ScrollbarStyle = platform.DefaultScrollbarStyle;
    }

    readonly HeadlessTextInput _textInput;

    /// <summary>可由测试脚本驱动的输入法桥。</summary>
    public override TextInputMethod TextInput => _textInput;

    /// <inheritdoc cref="TextInput"/>
    public HeadlessTextInput HeadlessTextInput => _textInput;

    public override Clipboard Clipboard => _platform.Clipboard;

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

    /// <summary>模拟窗口尺寸或缩放变化。</summary>
    public void Resize(Size size, double scale)
    {
        _size = size;
        _scale = scale;
        Events?.OnResized(size, scale);
    }

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
