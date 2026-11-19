using System;
using AwAUI.Browser.Interop;
using AwAUI.Graphics;
using AwAUI.Platform;

namespace AwAUI.Browser;

/// <summary>
/// 一个 &lt;canvas&gt; 元素对应的窗口。M0 采用软件呈现：框架渲染到 WASM 堆上的帧缓冲，
/// 提交时 JS 在线性内存上直接建 ImageData 视图并 putImageData（跨边界零拷贝，只有 putImageData 本身一次拷贝）。
/// WebGL2 呈现以后作为 <see cref="Surface"/> 的另一种能力加入。
/// <para>
/// 输入：canvas 上的 PointerEvent / WheelEvent；键盘、文本与输入法来自一个跟随光标的隐藏 &lt;textarea&gt;
/// （点击 canvas 时获得焦点）。帧节拍：<see cref="RequestFrame"/> 只登记需求，有需求且 canvas 可见时才挂
/// requestAnimationFrame（见 <see cref="FrameScheduler"/>）。尺寸：ResizeObserver（优先 device-pixel-content-box）
/// 加 devicePixelRatio 变化监听，见 <see cref="CanvasMetrics"/>。
/// </para>
/// </summary>
public sealed class BrowserWindow : WindowImpl, ITextInputSink
{
    readonly BrowserPlatform _platform;
    readonly BrowserWindowOptions _options;
    readonly BrowserSurface _surface;
    readonly FrameScheduler _scheduler = new();
    readonly CompositionTracker _composition;
    readonly bool _alpha;
    CanvasMetrics _metrics;
    string _cursor = "default";
    bool _closed;
    bool _focused;

    internal BrowserWindow(BrowserPlatform platform, int handle, string canvasId, BrowserWindowOptions options, in AttachResult initial)
    {
        _platform = platform;
        Handle = handle;
        CanvasId = canvasId;
        _options = options;
        _metrics = initial.Metrics;
        _alpha = initial.Alpha;
        _scheduler.SetVisible(initial.Visible); // 后台标签页中附加时一开始就不可见（此时没有挂起的回调，不需要 JS 操作）
        _surface = new BrowserSurface(this);
        _composition = new CompositionTracker(this);
        _textInput = new BrowserTextInput(this, _composition);
    }

    /// <summary>JS 端识别该窗口的句柄。</summary>
    public int Handle { get; }

    /// <summary>canvas 元素的 id。</summary>
    public string CanvasId { get; }

    public override NativeSurface Surface => _surface;
    public override Size ClientSize => _metrics.ClientSize;
    public override double RenderScale => _metrics.Scale;

    /// <summary>帧缓冲（canvas.width × canvas.height）的设备像素尺寸。</summary>
    public PixelSize PixelSize => _metrics.PixelSize;

    readonly BrowserTextInput _textInput;

    /// <summary>输入法与软键盘（<see cref="WindowImpl.TextInput"/> 的 Web 实现）。</summary>
    public override BrowserTextInput TextInput => _textInput;

    /// <summary>平台的剪贴板（全异步；剪贴板快捷键经浏览器的 copy / cut / paste 事件完成）。</summary>
    public override BrowserClipboard Clipboard => _platform.Clipboard;

    /// <summary>代理文本框是否有焦点（即窗口是否接收键盘输入）。</summary>
    public bool IsFocused => _focused;

    /// <summary>页面可见且 canvas 与视口相交；不可见时不出帧，请求保留到重新可见。</summary>
    public bool IsVisible => _scheduler.Visible;

    public bool IsClosed => _closed;

    /// <summary>
    /// 正在分发的按键事件的 KeyboardEvent.key（按当前布局的逻辑键，如 "a"、"Enter"、"Dead"）。
    /// 只在 <see cref="IWindowEvents.OnKey"/> 回调期间有效，其余时间为 null。与 <see cref="RawKeyEvent.LogicalKey"/> 相同（保留给已有调用方）。
    /// </summary>
    public string? CurrentKeyValue { get; private set; }

    /// <summary>已挂上 rAF 回调、尚未到达（诊断用：空闲时应为 false）。</summary>
    public bool IsFrameCallbackPending => _scheduler.CallbackPending;

    /// <summary>累计收到的 rAF 回调数与交付的 <see cref="IWindowEvents.OnFrame"/> 数。</summary>
    public long FrameCallbacks => _scheduler.CallbacksReceived;

    /// <inheritdoc cref="FrameCallbacks"/>
    public long FramesDelivered => _scheduler.FramesDelivered;

    /// <summary>呈现统计：最近一次 putImageData（含视图重建与必要时的 canvas 改尺寸）耗时、累计耗时与次数。</summary>
    public double LastPresentMilliseconds { get; private set; }

    /// <inheritdoc cref="LastPresentMilliseconds"/>
    public double TotalPresentMilliseconds { get; private set; }

    /// <inheritdoc cref="LastPresentMilliseconds"/>
    public long PresentCount { get; private set; }

    /// <summary>canvas 已在页面中，无需操作。</summary>
    public override void Show() { }

    public override void Close()
    {
        if (_closed) return;
        if (Events?.OnCloseRequested() == false) return;
        _closed = true;
        if (_scheduler.Close()) BrowserInterop.CancelFrame(Handle);
        _composition.Reset();
        BrowserInterop.Detach(Handle, _options.OwnsCanvas);
        _platform.Remove(this);
        Events?.OnClosed();
        _surface.Dispose();
    }

    public override void SetTitle(string title)
    {
        if (_closed || !_options.SetsDocumentTitle) return;
        BrowserInterop.SetTitle(title ?? "");
    }

    public override void SetCursor(CursorShape cursor)
    {
        if (_closed) return;
        string css = CursorCss.ToCss(cursor);
        if (css == _cursor) return; // 指针移动时每次都会设置光标，只在变化时跨边界
        _cursor = css;
        BrowserInterop.SetCursor(Handle, css);
    }

    public override void RequestFrame()
    {
        if (_closed) return;
        if (!_platform.Dispatcher.CheckAccess())
        {
            _platform.Dispatcher.Post(RequestFrame);
            return;
        }
        if (_scheduler.Request()) BrowserInterop.RequestFrame(Handle);
    }

    /// <summary>让窗口获得键盘焦点（聚焦代理文本框，不滚动页面）。</summary>
    public void Focus()
    {
        if (!_closed) BrowserInterop.Focus(Handle);
    }

    public override T? GetFeature<T>() where T : class => this as T ?? TextInput as T;

    /// <summary>文本输入客户端（获得焦点的可编辑元素）；没有时为 null。</summary>
    ITextInputClient? Client => TextInput.Client;

    // ---------------------------------------------------------------- 呈现

    internal void Present(Bitmap framebuffer)
    {
        if (_closed) return;
        var source = framebuffer;
        // 以 2D 上下文的实际 alpha 为准（页面可能先以 alpha: true 取过上下文，见 AttachResult.Alpha）
        if (_alpha) source = _surface.Unpremultiplied(framebuffer);
        double ms = BrowserInterop.Present(Handle, checked((int)source.Address), source.Width, source.Height);
        LastPresentMilliseconds = ms;
        TotalPresentMilliseconds += ms;
        PresentCount++;
    }

    // ---------------------------------------------------------------- JS 回调

    internal void HandleAnimationFrame(double timeMs)
    {
        if (!_scheduler.OnAnimationFrame(timeMs, out var timestamp)) return;
        Events?.OnFrame(timestamp);
    }

    internal void HandleResize(double cssWidth, double cssHeight, double devicePixelRatio, double deviceWidth, double deviceHeight)
    {
        if (_closed) return;
        var metrics = CanvasMetrics.Compute(cssWidth, cssHeight, devicePixelRatio, deviceWidth, deviceHeight);
        if (metrics == _metrics) return;
        _metrics = metrics;
        Events?.OnResized(metrics.ClientSize, metrics.Scale);
        RequestFrame(); // 新尺寸的帧缓冲需要重画；canvas 本身在呈现时才改尺寸，期间旧内容保持显示
    }

    internal void HandlePointer(in DomPointerSample sample)
    {
        if (_closed) return;
        if (PointerTranslator.TryTranslate(sample, ClientSize, out var e)) Events?.OnPointer(e);
    }

    internal void HandleWheel(in DomWheelSample sample)
    {
        if (_closed) return;
        var e = PointerTranslator.TranslateWheel(sample, ClientSize);
        Events?.OnPointer(e);
    }

    internal bool HandleKey(bool down, string code, string key, int keyCode, int modifiers, bool repeat, bool composing, double timeMs)
    {
        if (_closed) return false;
        var physical = DomKeyMap.ToPhysicalKey(code);
        var policyMods = DomKeyMap.ToModifiers(modifiers);
        var logical = DomKeyMap.ToLogicalKey(key);
        if (KeyboardPolicy.ShouldForward(composing, keyCode))
        {
            CurrentKeyValue = logical;
            try
            {
                Events?.OnKey(new RawKeyEvent(down ? RawKeyEventType.Down : RawKeyEventType.Up, physical, DomKeyMap.ToKeyModifiers(modifiers, logical),
                    repeat && down, keyCode, DomProtocol.ToTimeSpan(timeMs), logical));
            }
            finally { CurrentKeyValue = null; }
        }
        return KeyboardPolicy.ShouldPreventDefault(physical, policyMods, composing, keyCode, key, (modifiers & DomProtocol.ModAltGraph) != 0);
    }

    internal bool HandleBeforeInput(string inputType, string? data, bool composing)
    {
        if (_closed) return false;
        var client = Client;
        var action = TextInputFilter.Classify(inputType, composing, client is not null);
        switch (action)
        {
            case BeforeInputAction.PreventAndCommit:
                HandleText(data);
                break;
            case BeforeInputAction.PreventAndDeleteBackward:
            case BeforeInputAction.PreventAndDeleteForward:
                if (client is not null) DeleteAroundCaret(client, action == BeforeInputAction.PreventAndDeleteBackward);
                break;
            case BeforeInputAction.PreventAndEnter:
            {
                // 移动端键盘只送出 insertParagraph：合成一次回车按键，框架按回车处理（提交、换行）
                var t = DomProtocol.ToTimeSpan(BrowserInterop.Now());
                Events?.OnKey(new RawKeyEvent(RawKeyEventType.Down, PhysicalKey.Enter, KeyModifiers.None, false, 13, t, KeyValues.Enter));
                if (!_closed) Events?.OnKey(new RawKeyEvent(RawKeyEventType.Up, PhysicalKey.Enter, KeyModifiers.None, false, 13, t, KeyValues.Enter));
                break;
            }
        }
        return action != BeforeInputAction.PassThrough;
    }

    static void DeleteAroundCaret(ITextInputClient client, bool backward)
    {
        var sel = client.Selection;
        int len = client.Length;
        string before = client.GetText(new TextInputRange(Math.Max(0, sel.Start - 2), sel.Start));
        string after = client.GetText(new TextInputRange(sel.End, Math.Min(len, sel.End + 2)));
        var range = TextInputFilter.DeletionRange(sel, len, backward, before, after);
        if (!range.IsEmpty) client.ReplaceRange(range, string.Empty);
    }

    internal void HandleText(string? text)
    {
        if (_closed) return;
        var s = TextInputFilter.SanitizeCommit(text);
        if (s.Length == 0) return;
        if (Client is { } client) client.Commit(s);
        else Events?.OnTextInput(s);
    }

    internal void HandleComposition(int kind, string? data, int selectionStart, int selectionEnd)
    {
        if (_closed) return;
        _composition.Handle(kind, data, selectionStart, selectionEnd);
    }

    internal void HandleFocus(bool focused)
    {
        if (_closed || focused == _focused) return;
        _focused = focused;
        Events?.OnFocusChanged(focused);
    }

    internal void HandleVisibility(bool visible)
    {
        if (_closed) return;
        switch (_scheduler.SetVisible(visible))
        {
            case VisibilityAction.Schedule: BrowserInterop.RequestFrame(Handle); break;
            case VisibilityAction.Cancel: BrowserInterop.CancelFrame(Handle); break;
        }
    }

    internal void HandlePaste(string text)
    {
        if (_closed) return;
        var s = TextInputFilter.SanitizeCommit(text);
        if (s.Length == 0) return;
        if (_platform.Clipboard.RaisePasted(this, s)) return;
        if (Client is { } client) client.ReplaceRange(client.Selection, s);
        else Events?.OnTextInput(s);
    }

    /// <summary>copy / cut 事件：应用的处理器优先，其次是文本输入客户端的选中文本（剪切时删除它）。返回 null 表示不处理。</summary>
    internal string? HandleCopy(bool cut)
    {
        if (_closed) return null;
        if (_platform.Clipboard.CopyHandler is { } handler) return handler(this, cut);
        if (Client is not { } client) return null;
        var sel = client.Selection;
        if (sel.IsEmpty || client.Options.IsPassword) return null;
        string text = client.GetText(sel);
        if (cut) client.ReplaceRange(sel, string.Empty);
        return text;
    }

    void ITextInputSink.OnComposition(in TextCompositionEvent e) => TextInput.OnComposition(e);

    void ITextInputSink.OnCommit(string text) => HandleText(text);

    /// <summary>软件呈现用的帧缓冲表面：位图分配在 WASM 线性内存中，尺寸跟随 canvas 的设备像素尺寸。</summary>
    sealed class BrowserSurface : NativeSurface, IFramebufferSurface, IDisposable
    {
        readonly BrowserWindow _owner;
        Bitmap? _framebuffer;
        Bitmap? _staging;

        public BrowserSurface(BrowserWindow owner) => _owner = owner;

        public override PixelSize Size => _owner._metrics.PixelSize;
        public override double Scale => _owner._metrics.Scale;

        public ILockedFramebuffer Lock()
        {
            if (_owner._closed) throw new ObjectDisposedException(nameof(BrowserWindow));
            var size = Size;
            if (size.IsEmpty) size = new PixelSize(1, 1);
            if (_framebuffer == null || _framebuffer.Size != size)
            {
                _framebuffer?.Dispose();
                _framebuffer = null;
                _framebuffer = new Bitmap(size);
            }
            return new Locked(_framebuffer, _owner);
        }

        /// <summary>透明 canvas：反预乘到暂存位图（ImageData 是非预乘的）。</summary>
        public unsafe Bitmap Unpremultiplied(Bitmap source)
        {
            if (_staging == null || _staging.Size != source.Size)
            {
                _staging?.Dispose();
                _staging = null;
                _staging = new Bitmap(source.Size);
            }
            int bytes = checked(source.RowBytes * source.Height);
            PixelConversion.UnpremultiplyRgba(new ReadOnlySpan<byte>((void*)source.Address, bytes), new Span<byte>((void*)_staging.Address, bytes));
            return _staging;
        }

        public void Dispose()
        {
            _framebuffer?.Dispose();
            _framebuffer = null;
            _staging?.Dispose();
            _staging = null;
        }

        sealed class Locked : ILockedFramebuffer
        {
            readonly Bitmap _bitmap;
            readonly BrowserWindow _owner;
            bool _disposed;

            public Locked(Bitmap bitmap, BrowserWindow owner) { _bitmap = bitmap; _owner = owner; }
            public IntPtr Address => _bitmap.Address;
            public PixelSize Size => _bitmap.Size;
            public int RowBytes => _bitmap.RowBytes;
            public PixelFormat Format => _bitmap.Format;

            /// <summary>提交：立即呈现到 canvas（浏览器在下一次合成时显示）。</summary>
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _owner.Present(_bitmap);
            }
        }
    }
}
