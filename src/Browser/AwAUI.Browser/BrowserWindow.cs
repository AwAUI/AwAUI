using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.JavaScript;
using System.Threading.Tasks;
using AwAUI.Browser.Accessibility;
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
    bool _activated = true;
    bool _accessibilityCreated;
    BrowserAccessibilityBridge? _accessibility;
    WindowState _windowState;
    AppLifecycle _lifecycle;
    Thickness _safeArea;

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
        // 焦点进入 / 离开可编辑元素（或输入类型变化）时，把客户端选项里的软键盘提示同步给代理文本框
        _textInput.ClientChanged += _ => ApplyTextInputOptions();
        // 浏览器把窗口状态与页面生命周期作为全局事件报告；这里只取初始值，之后由 JS 回调更新。
        try
        {
            _activated = BrowserInterop.HasFocus();
            _windowState = BrowserInterop.IsFullscreen() ? WindowState.FullScreen : WindowState.Normal;
            _safeArea = ReadSafeArea();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _activated = true;
            _windowState = WindowState.Normal;
            _safeArea = Thickness.Zero;
        }
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

    /// <summary>浏览器只暴露一个逻辑显示器，见 <see cref="BrowserScreens"/>。</summary>
    public override Screens Screens => _platform.Screens;

    /// <summary>文件选择器：隐藏的 &lt;input type=file&gt; 与 File System Access / 下载回退。</summary>
    public override StorageProvider StorageProvider => _platform.StorageProvider;

    /// <summary>用新的浏览上下文打开 http / https / mailto URI。</summary>
    public override Launcher Launcher => _platform.Launcher;

    /// <summary>
    /// Accessibility bridge for the Web platform: a canvas is opaque to screen readers, so the bridge keeps a
    /// shadow ARIA DOM in sync with the semantic tree. Created on first access; returns null when
    /// <c>AWAUI_BROWSER_A11Y=0</c> or when the page disables it (the semantic tree then stays lazy).
    /// </summary>
    public override AccessibilityBridge? Accessibility
    {
        get
        {
            if (!_accessibilityCreated)
            {
                _accessibilityCreated = true;
                _accessibility = BrowserAccessibilityBridge.TryCreate(Handle);
            }
            return _accessibility;
        }
    }

    /// <summary>浏览器不报告窗口位置（canvas 在页面布局中），返回 (0, 0)。</summary>
    public override Point Position => default;

    /// <summary>浏览器无法移动页面中的 canvas；忽略（需要定位时用 <see cref="WindowOptions.StartupLocation"/> 的尽力滚动）。</summary>
    public override void SetPosition(Point position) { }

    /// <summary>Normal 或 FullScreen（由 <c>document.fullscreenElement</c> 报告）；浏览器没有最小化 / 最大化窗口概念。</summary>
    public override WindowState WindowState => _windowState;

    /// <summary>
    /// 切换全屏：FullScreen 调用 canvas 的 <c>requestFullscreen</c>，Normal 退出全屏。
    /// 浏览器通常要求用户手势，失败时保持原状态；Minimized / Maximized 被忽略（不支持，也不假装支持）。
    /// </summary>
    public override void SetWindowState(WindowState state)
    {
        if (_closed) return;
        switch (state)
        {
            case WindowState.FullScreen:
                _ = SetFullscreenAsync(true);
                break;
            case WindowState.Normal:
                _ = SetFullscreenAsync(false);
                break;
            default:
                return;
        }
    }

    /// <summary>窗口所在的页面是否获得焦点（<c>window</c> 的 focus / blur 事件）。</summary>
    public override bool IsActive => _activated;

    /// <summary>聚焦代理文本框（键盘与输入法事件的来源），并尽力把页面窗口提到前台。</summary>
    public override void Activate()
    {
        if (_closed) return;
        try { BrowserInterop.ActivateWindow(Handle); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 页面焦点由浏览器控制；失败时保持现状。
        }
    }

    /// <summary>安全区来自 CSS <c>env(safe-area-inset-*)</c>；桌面浏览器上没有刘海时为 0。</summary>
    public override Thickness SafeAreaInsets => _safeArea;

    async Task SetFullscreenAsync(bool fullscreen)
    {
        try
        {
            bool ok = fullscreen
                ? await BrowserInterop.RequestFullscreen(Handle).ConfigureAwait(true)
                : await BrowserInterop.ExitFullscreen().ConfigureAwait(true);
            // fullscreenchange 通常已经报告过；这里覆盖没有派发事件（或事件先于本回调返回）的情况。
            if (ok) HandleFullscreen(fullscreen);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 权限 / 用户手势 / 策略拒绝：状态不变。
        }
    }

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

    /// <summary>
    /// 把 <see cref="WindowOptions"/> 里浏览器能表达的部分交给 JS：最小 / 最大尺寸用 canvas 的 CSS 限制；
    /// <see cref="WindowStartupLocation.CenterScreen"/> / <see cref="WindowStartupLocation.CenterOwner"/> 与
    /// <see cref="WindowOptions.Position"/> 通过滚动页面把 canvas 移到视口（尽力而为，页面不能滚动时无效果）；
    /// <see cref="WindowState.FullScreen"/> 请求全屏。Topmost / Icon / ShowInTaskbar / SizeToContent 在浏览器中无对应能力，忽略。
    /// </summary>
    internal void ApplyOptions(WindowOptions options)
    {
        if (_closed) return;
        var position = options.Position;
        try
        {
            BrowserInterop.ApplyWindowOptions(
                Handle,
                (int)options.StartupLocation,
                position.HasValue,
                position?.X ?? 0,
                position?.Y ?? 0,
                options.State == WindowState.FullScreen,
                options.MinWidth ?? -1,
                options.MinHeight ?? -1,
                options.MaxWidth ?? -1,
                options.MaxHeight ?? -1);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 页面布局与全屏都由浏览器控制；失败时保持默认。
        }
    }

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
        // 旋转 / 视口变化时 env(safe-area-inset-*) 可能变化；只在真的变化时通知。
        RefreshSafeArea();
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

    /// <summary>
    /// 把当前文本输入客户端的选项作为提示同步给隐藏的代理文本框：
    /// <see cref="TextInputOptions.LocaleHints"/> 首项 → <c>lang</c>，<see cref="TextInputOptions.ContentType"/> / 多行 → <c>inputmode</c>，
    /// <see cref="TextInputOptions.ReturnKeyType"/> → <c>enterkeyhint</c>。其余选项（自动更正、建议、敏感内容）桌面浏览器无法表达，只作为提示。
    /// </summary>
    void ApplyTextInputOptions()
    {
        if (_closed) return;
        if (_textInput.Client is { } client)
        {
            var options = client.Options;
            BrowserTextInputHints.SetTextInputOptions(Handle, FirstLocaleHint(options.LocaleHints), InputModeFor(options), EnterKeyHintFor(options));
        }
        else
        {
            BrowserTextInputHints.SetTextInputOptions(Handle, null, "text", null);
        }
    }

    static string? FirstLocaleHint(IReadOnlyList<string>? hints) =>
        hints is { Count: > 0 } && !string.IsNullOrWhiteSpace(hints[0]) ? hints[0] : null;

    static string InputModeFor(in TextInputOptions options)
    {
        // 多行输入总是用 text（软键盘回车插入换行）；单行按内容类型选择键盘布局
        if (options.Multiline) return "text";
        return options.ContentType switch
        {
            TextInputContentType.Number => "numeric",
            TextInputContentType.Email => "email",
            TextInputContentType.Url => "url",
            TextInputContentType.Search => "search",
            TextInputContentType.Phone => "tel",
            // 密码与普通文本都没有专用的 inputmode：浏览器不会把 textarea 当成密码框
            _ => "text",
        };
    }

    static string? EnterKeyHintFor(in TextInputOptions options) => options.ReturnKeyType switch
    {
        TextInputReturnKeyType.Done => "done",
        TextInputReturnKeyType.Go => "go",
        TextInputReturnKeyType.Next => "next",
        TextInputReturnKeyType.Return => "enter",
        TextInputReturnKeyType.Search => "search",
        TextInputReturnKeyType.Send => "send",
        _ => null, // Default：不设置，由浏览器决定
    };

    internal void HandleFocus(bool focused)
    {
        if (_closed || focused == _focused) return;
        _focused = focused;
        Events?.OnFocusChanged(focused);
    }

    /// <summary>页面 / 窗口级激活（window 的 focus / blur，页面隐藏时视为失活）。</summary>
    internal void HandleActivation(bool active)
    {
        if (_closed || active == _activated) return;
        _activated = active;
        if (Events is IWindowLifecycleEvents lifecycle)
        {
            lifecycle.OnActivated(active);
            lifecycle.OnLifecycle(active ? AppLifecycle.Active : AppLifecycle.Inactive);
        }
    }

    /// <summary>visibilitychange / pagehide / pageshow 映射到 <see cref="AppLifecycle"/>；未知数值被忽略为 Unknown。</summary>
    internal void HandleLifecycle(int state)
    {
        if (_closed) return;
        if (state < (int)AppLifecycle.Unknown || state > (int)AppLifecycle.Foreground) state = (int)AppLifecycle.Unknown;
        var next = (AppLifecycle)state;
        if (next == _lifecycle) return;
        _lifecycle = next;
        (Events as IWindowLifecycleEvents)?.OnLifecycle(next);
    }

    /// <summary>document 的全屏状态变化：Normal ↔ FullScreen；浏览器没有其它窗口状态。</summary>
    internal void HandleFullscreen(bool fullscreen)
    {
        if (_closed) return;
        var next = fullscreen ? WindowState.FullScreen : WindowState.Normal;
        if (next == _windowState) return;
        _windowState = next;
        (Events as IWindowStateEvents)?.OnWindowStateChanged(next);
    }

    Thickness ReadSafeArea()
    {
        try
        {
            var value = BrowserInterop.SafeArea(Handle);
            return value is { Length: >= 4 }
                ? new Thickness(value[0], value[1], value[2], value[3])
                : Thickness.Zero;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Thickness.Zero;
        }
    }

    void RefreshSafeArea()
    {
        var next = ReadSafeArea();
        if (next == _safeArea) return;
        _safeArea = next;
        (Events as IWindowLifecycleEvents)?.OnSafeAreaChanged(next);
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

/// <summary>
/// 代理 &lt;textarea&gt; 的软键盘提示（JS 端 <c>setTextInputOptions</c>）。这些选项在桌面浏览器上只是提示：
/// 浏览器只表达语言（<c>lang</c>）、键盘布局（<c>inputmode</c>）与回车键样式（<c>enterkeyhint</c>）。
/// </summary>
internal static partial class BrowserTextInputHints
{
    [JSImport("setTextInputOptions", BrowserInterop.ModuleName)]
    internal static partial void SetTextInputOptions(int handle, string? lang, string inputMode, string? enterKeyHint);
}
