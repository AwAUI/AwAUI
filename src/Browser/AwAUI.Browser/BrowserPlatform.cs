using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.JavaScript;
using System.Threading;
using System.Threading.Tasks;
using AwAUI.Browser.Interop;
using AwAUI.Platform;
using AwAUI.Threading;

namespace AwAUI.Browser;

/// <summary>
/// Web 平台后端（design/04「Web 平台的特殊性」）：每个 &lt;canvas&gt; 是一个 <see cref="BrowserWindow"/>，
/// 输入来自 DOM 事件，帧节拍来自 requestAnimationFrame，JS 互操作全部经 [JSImport]/[JSExport]。
/// <para>
/// 浏览器的消息循环由页面自己驱动：<see cref="Run"/> 立即返回，<see cref="RunAsync"/> 返回一个在 <see cref="Exit"/>
/// 时完成的任务。单线程 WASM 下所有回调都在主线程上发生。每个进程（页面中的 .NET 运行时）只能创建一个实例。
/// </para>
/// </summary>
public sealed class BrowserPlatform : PlatformBackend
{
    static BrowserPlatform? s_current;

    readonly BrowserDispatcher _dispatcher = new();
    readonly Dictionary<int, BrowserWindow> _windows = new();
    readonly TaskCompletionSource<bool> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly BrowserClipboard _clipboard = new();
    int _nextHandle = 1;

    BrowserPlatform() { }

    internal static BrowserPlatform? Current => s_current;

    /// <summary>
    /// 加载 JS 胶水模块并创建平台。<paramref name="glueModuleUrl"/> 默认指向本包的静态 Web 资产
    /// （<c>_content/AwAUI.Browser/awaui-browser.js</c>）；相对 URL 以 <c>_framework/</c> 为基准。
    /// </summary>
    /// <exception cref="InvalidOperationException">已经创建过实例。</exception>
    /// <exception cref="PlatformNotSupportedException">不在浏览器（或 Node 等 JS 宿主）的 WASM 运行时中。</exception>
    public static async Task<BrowserPlatform> CreateAsync(string? glueModuleUrl = null)
    {
        if (!OperatingSystem.IsBrowser()) throw new PlatformNotSupportedException("BrowserPlatform requires the browser-wasm runtime.");
        if (s_current != null) throw new InvalidOperationException("Only one BrowserPlatform may be created per runtime.");
        var platform = new BrowserPlatform();
        s_current = platform;
        try
        {
            await JSHost.ImportAsync(BrowserInterop.ModuleName, glueModuleUrl ?? BrowserInterop.DefaultModuleUrl).ConfigureAwait(true);
            await BrowserInterop.Init().ConfigureAwait(true);
        }
        catch
        {
            s_current = null;
            throw;
        }
        return platform;
    }

    public override string Name => "Browser";
    public override IDispatcher Dispatcher => _dispatcher;

    /// <summary>已附加且未关闭的窗口（快照：遍历时关闭窗口是安全的）。</summary>
    public IReadOnlyCollection<BrowserWindow> Windows => new List<BrowserWindow>(_windows.Values);

    /// <summary>
    /// 把页面中 id 为 <paramref name="canvasId"/> 的 &lt;canvas&gt; 作为窗口。canvas 应没有边框与内边距
    /// （逻辑尺寸取其 CSS 内容盒，指针坐标按边框盒换算）。
    /// </summary>
    /// <exception cref="ArgumentException">找不到该 id 的 canvas 元素。</exception>
    /// <exception cref="InvalidOperationException">该 canvas 已附加为另一个窗口，或取不到它的 2D 上下文（已有 WebGL 上下文等）。</exception>
    public BrowserWindow AttachCanvas(string canvasId, BrowserWindowOptions? options = null)
    {
        if (canvasId is null) throw new ArgumentNullException(nameof(canvasId));
        _dispatcher.VerifyAccess();
        options ??= new BrowserWindowOptions();
        int handle = _nextHandle++;
        int flags = (options.PreventContextMenu ? 1 : 0) | (options.UseCoalescedEvents ? 2 : 0) | (options.PauseWhenOffscreen ? 4 : 0);
        var initial = AttachResult.Parse(BrowserInterop.AttachCanvas(handle, canvasId, options.Transparent, flags), options.Transparent);
        switch (initial.Status)
        {
            case AttachStatus.NotFound: throw new ArgumentException($"No <canvas> element with id '{canvasId}'.", nameof(canvasId));
            case AttachStatus.AlreadyAttached: throw new InvalidOperationException($"The <canvas> '{canvasId}' is already attached to a window.");
            case AttachStatus.NoContext: throw new InvalidOperationException($"Cannot get a 2D context for <canvas> '{canvasId}' (it already has another context type).");
        }
        var window = new BrowserWindow(this, handle, canvasId, options, initial);
        _windows.Add(handle, window);
        return window;
    }

    /// <summary>
    /// 在 body 末尾新建一个 CSS 尺寸为 <see cref="WindowOptions.Size"/> 的 canvas 并附加。
    /// 需要嵌入页面布局时改用 <see cref="AttachCanvas"/>。
    /// </summary>
    public override WindowImpl CreateWindow(WindowOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        _dispatcher.VerifyAccess();
        string id = BrowserInterop.CreateCanvas(options.Size.Width, options.Size.Height);
        var window = AttachCanvas(id, new BrowserWindowOptions { OwnsCanvas = true });
        window.SetTitle(options.Title);
        return window;
    }

    /// <summary>浏览器的事件循环不归 .NET 所有：立即返回。需要等待退出时用 <see cref="RunAsync"/>。</summary>
    public override void Run() { }

    /// <summary>返回在 <see cref="Exit"/> 被调用时完成的任务。</summary>
    public Task RunAsync() => _exit.Task;

    public override void Exit() => _exit.TrySetResult(true);

    public override T? GetService<T>() where T : class => _clipboard as T;

    /// <summary>剪贴板（异步 API，尽力而为：读取需要权限或用户手势）。</summary>
    public override BrowserClipboard Clipboard => _clipboard;

    internal BrowserWindow? FindWindow(int handle) => _windows.TryGetValue(handle, out var w) ? w : null;

    internal void Remove(BrowserWindow window) => _windows.Remove(window.Handle);

    internal void DrainDispatcher() => _dispatcher.Drain();
}

/// <summary>
/// 主线程调度器。投递的操作排队，经 JS 的 MessageChannel 宏任务执行（不阻塞当前事件处理，也不像 setTimeout(0)
/// 那样被浏览器节流到 4ms）。JS 互操作只能在主线程调用：多线程 WASM 下从其他线程投递时，
/// 经主线程的 SynchronizationContext 转到主线程再排 drain（单线程 WASM 不存在其他线程）。
/// </summary>
internal sealed class BrowserDispatcher : IDispatcher
{
    readonly int _threadId = Environment.CurrentManagedThreadId;
    readonly SynchronizationContext? _mainContext = SynchronizationContext.Current;
    readonly DispatchQueue _queue;

    public BrowserDispatcher() => _queue = new DispatchQueue(RequestDrain);

    public bool CheckAccess() => Environment.CurrentManagedThreadId == _threadId;

    public void Post(Action action) => _queue.Enqueue(action);

    /// <summary>由 JS 的 drain 宏任务调用。操作抛出的异常照常传播（成为页面的未处理错误），剩余操作安排到下一次 drain。</summary>
    public void Drain() => _queue.Drain();

    void RequestDrain()
    {
        if (CheckAccess()) BrowserInterop.ScheduleDrain();
        else if (_mainContext != null) _mainContext.Post(static _ => BrowserInterop.ScheduleDrain(), null);
    }
}

/// <summary>附加 canvas 时的选项。</summary>
public sealed class BrowserWindowOptions
{
    /// <summary>
    /// canvas 是否透明（2D 上下文 alpha: true）。默认不透明：预乘帧缓冲可直接 putImageData；
    /// 透明时每帧多一遍反预乘。页面先以 alpha: true 取过该 canvas 的 2D 上下文时，浏览器返回那个已有的上下文，
    /// 此时无论本选项如何都按透明处理。
    /// </summary>
    public bool Transparent { get; set; }

    /// <summary>阻止 canvas 上的浏览器右键菜单（默认 true，右键交给框架）。</summary>
    public bool PreventContextMenu { get; set; } = true;

    /// <summary>pointermove 时上交浏览器合并掉的全部采样（getCoalescedEvents，design/09）。默认 true。</summary>
    public bool UseCoalescedEvents { get; set; } = true;

    /// <summary>canvas 滚出视口时暂停出帧（IntersectionObserver）。默认 true。</summary>
    public bool PauseWhenOffscreen { get; set; } = true;

    /// <summary><see cref="BrowserWindow.SetTitle"/> 是否修改 document.title。默认 true。</summary>
    public bool SetsDocumentTitle { get; set; } = true;

    /// <summary>关闭窗口时从 DOM 移除 canvas（由 <see cref="BrowserPlatform.CreateWindow"/> 创建的 canvas）。</summary>
    internal bool OwnsCanvas { get; set; }
}
