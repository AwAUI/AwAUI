using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AwAUI.MacOS.Interop;
using AwAUI.Platform;
using AwAUI.Threading;
using static AwAUI.MacOS.Interop.ObjC;

namespace AwAUI.MacOS;

/// <summary>
/// macOS 平台后端：通过 Objective-C 运行时直接驱动 AppKit，不依赖 net-macos workload（design/04 D13）。
/// 必须在主线程上创建并运行。
/// </summary>
public sealed class MacPlatform : PlatformBackend
{
    internal const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    static MacPlatform? s_instance;

    readonly MacDispatcher _dispatcher;
    readonly Dictionary<IntPtr, MacWindow> _windowsByHandle = new();
    readonly List<MacWindow> _windows = new();
    readonly IntPtr _app;
    readonly IntPtr _helper;
    readonly MacReducedMotionPreference? _reducedMotion;
    readonly MacAppearancePreference? _appearance;

    /// <summary>帧时间戳的原点（主机时间，秒）：OnFrame 收到的是相对平台创建时刻的时间。</summary>
    internal readonly double Epoch;

    public MacPlatform()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) throw new PlatformNotSupportedException("MacPlatform requires macOS.");
        Epoch = HostTime.Now; // 必须在平台检查之后：HostTime 依赖 macOS 专有的库
        if (s_instance != null) throw new InvalidOperationException("Only one MacPlatform may be created per process.");
        CG.LoadFramework(AppKit);
        if (SendByte(Class("NSThread"), Sel("isMainThread")) == 0)
            throw new InvalidOperationException("MacPlatform must be created on the main thread.");
        s_instance = this;

        var pool = objc_autoreleasePoolPush();
        RegisterClasses();
        _app = Send(Class("NSApplication"), Sel("sharedApplication"));
        Send(_app, Sel("setActivationPolicy:"), 0L); // NSApplicationActivationPolicyRegular
        _helper = Send(Send(Class("AwAUIHelper"), Sel("alloc")), Sel("init"));
        _dispatcher = new MacDispatcher(_helper);
        _reducedMotion = MacReducedMotionPreference.TryCreate();
        _reducedMotion?.Observe(_helper);
        _appearance = MacAppearancePreference.TryCreate();
        _appearance?.Observe(_helper);
        ObserveNotifications();
        BuildMenu();
        objc_autoreleasePoolPop(pool);
    }

    public override string Name => "macOS";

    MacClipboard? _clipboard;
    MacScreens? _screens;
    MacStorageProvider? _storageProvider;
    MacLauncher? _launcher;

    /// <summary>系统剪贴板（NSPasteboard）。</summary>
    public override Clipboard Clipboard => _clipboard ??= new MacClipboard(this);

    /// <summary>显示器集合（NSScreen）；布局变化时触发 <see cref="Screens.Changed"/>。</summary>
    public override Screens Screens => MacScreensService;

    /// <summary>文件与目录选择器（NSOpenPanel / NSSavePanel）。</summary>
    public override StorageProvider StorageProvider => _storageProvider ??= new MacStorageProvider(this);

    /// <summary>用系统默认程序打开 URI / 文件 / 目录（NSWorkspace）。</summary>
    public override Launcher Launcher => _launcher ??= new MacLauncher(this);

    public override IDispatcher Dispatcher => _dispatcher;
    public IReadOnlyList<MacWindow> Windows => _windows;
    internal IReducedMotionPreference? ReducedMotionPreference => _reducedMotion;
    internal IAppearancePreference? AppearancePreference => _appearance;

    internal MacScreens MacScreensService => _screens ??= new MacScreens(this);

    /// <summary>主显示器在 Cocoa 坐标中的上边，窗口位置换算使用。</summary>
    internal double PrimaryScreenTop => MacScreensService.PrimaryTop;

    /// <summary><c>[NSApplication sharedApplication]</c>（应用级图标、激活等）。</summary>
    internal IntPtr Application => _app;

    public override T? GetService<T>() where T : class
    {
        if (this is T self) return self;
        return Clipboard as T ?? Screens as T ?? StorageProvider as T ?? Launcher as T ?? _dispatcher as T;
    }

    public override WindowImpl CreateWindow(WindowOptions options)
    {
        var w = new MacWindow(this, options);
        _windows.Add(w);
        return w;
    }

    internal void Register(IntPtr handle, MacWindow window) => _windowsByHandle[handle] = window;
    internal void Unregister(IntPtr handle) => _windowsByHandle.Remove(handle);

    internal void OnWindowClosed(MacWindow w)
    {
        _windows.Remove(w);
        if (_windows.Count == 0) Exit();
    }

    bool _running;

    public override void Run()
    {
        _running = true;
        SendBool(_app, Sel("activateIgnoringOtherApps:"), 1);
        Send(_app, Sel("run"));
        _running = false;
    }

    public override void Exit()
    {
        if (!_running) return;
        Send(_app, Sel("stop:"), IntPtr.Zero);
        // stop: 在处理完下一个事件后才生效，投递一个空事件唤醒消息循环
        var ev = SendEvent(Class("NSEvent"), Sel("otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:"),
            15 /* NSEventTypeApplicationDefined */, default, 0, 0, 0, IntPtr.Zero, 0, 0, 0);
        Send(_app, Sel("postEvent:atStart:"), ev, (IntPtr)1);
    }

    /// <summary>
    /// 观察应用级通知：前后台激活（转成 <see cref="IWindowLifecycleEvents.OnActivated"/> / <see cref="IWindowLifecycleEvents.OnLifecycle"/>）
    /// 与显示器布局变化（刷新 <see cref="Screens"/> 并触发 <see cref="Screens.Changed"/>）。
    /// </summary>
    void ObserveNotifications()
    {
        var center = Send(Class("NSNotificationCenter"), Sel("defaultCenter"));
        Observe(center, "NSApplicationDidBecomeActiveNotification", Sel("awAppDidBecomeActive:"));
        Observe(center, "NSApplicationDidResignActiveNotification", Sel("awAppDidResignActive:"));
        Observe(center, "NSApplicationDidChangeScreenParametersNotification", Sel("awScreensChanged:"));
    }

    void Observe(IntPtr center, string notificationName, IntPtr selector)
    {
        var name = CG.GetConstant(AppKit, notificationName);
        if (name != IntPtr.Zero)
            SendVoid(center, Sel("addObserver:selector:name:object:"), _helper, selector, name, IntPtr.Zero);
    }

    void SetApplicationActive(bool active)
    {
        // 回调可能关闭窗口（OnActivated 里调用 Window.Close）：倒序遍历，避免集合在枚举中变化
        for (int i = _windows.Count - 1; i >= 0; i--) _windows[i].NotifyApplicationActive(active);
    }

    void BuildMenu()
    {
        var mainMenu = Send(Send(Class("NSMenu"), Sel("alloc")), Sel("init"));
        var appItem = Send(Send(Class("NSMenuItem"), Sel("alloc")), Sel("init"));
        Send(mainMenu, Sel("addItem:"), appItem);
        var appMenu = Send(Send(Class("NSMenu"), Sel("alloc")), Sel("init"));
        var title = NewString("Quit");
        var key = NewString("q");
        var quit = Send(Send(Class("NSMenuItem"), Sel("alloc")), Sel("initWithTitle:action:keyEquivalent:"), title, Sel("awQuit:"), key);
        Send(quit, Sel("setTarget:"), _helper);
        Send(appMenu, Sel("addItem:"), quit);
        Send(appItem, Sel("setSubmenu:"), appMenu);
        Send(_app, Sel("setMainMenu:"), mainMenu);
        Release(title); Release(key);
    }

    static void RegisterClasses()
    {
        Callbacks.OnEvent = (self, sel, arg) => s_instance?.HandleEvent(self, sel, arg);
        Callbacks.OnDrawRect = (self, rect) => s_instance?.Find(self)?.DrawRect();
        // acceptsFirstResponder / acceptsFirstMouse 为 YES；NSTextInputClient 的 hasMarkedText 按窗口状态
        Callbacks.OnQueryBool = (self, sel) => sel == Sel("hasMarkedText") ? s_instance?.Find(self)?.HasMarkedText ?? false : true;
        Callbacks.OnInsertText = (self, text, range) => s_instance?.Find(self)?.InsertText(text, range);
        Callbacks.OnSetMarkedText = (self, text, selected, replacement) => s_instance?.Find(self)?.SetMarkedText(text, selected, replacement);
        Callbacks.OnVoid = (self, sel) =>
        {
            if (sel == Sel("unmarkText")) s_instance?.Find(self)?.UnmarkText();
        };
        Callbacks.OnQueryRange = (self, sel) =>
        {
            var w = s_instance?.Find(self);
            if (w is null) return new NSRange { Location = Callbacks.NotFound };
            return sel == Sel("markedRange") ? w.MarkedRange : w.SelectedRange;
        };
        Callbacks.OnQueryObject = (self, sel) => MacWindow.EmptyArray(); // validAttributesForMarkedText：不使用富文本属性
        Callbacks.OnAttributedSubstring = (self, range, actual) => s_instance?.Find(self)?.AttributedSubstring(range, actual) ?? IntPtr.Zero;
        Callbacks.OnFirstRect = (self, range, actual) => s_instance?.Find(self)?.FirstRect(range, actual) ?? default;
        Callbacks.OnCharacterIndex = (self, point) =>
            s_instance?.Find(self)?.CharacterIndex(point) ?? Callbacks.NotFound;
        Callbacks.OnQueryBoolArg = (self, sel, arg) =>
            sel == Sel("windowShouldClose:") ? s_instance?.Find(self)?.ShouldClose() ?? true : true;
        Callbacks.OnPanelEnd = (panel, code, context) => MacStorageProvider.OnPanelEnd(panel, code, context);

        const string ev = "v@:@";
        var viewEvents = new[]
        {
            "mouseDown:", "mouseUp:", "mouseDragged:", "mouseMoved:", "mouseEntered:", "mouseExited:",
            "rightMouseDown:", "rightMouseUp:", "rightMouseDragged:", "otherMouseDown:", "otherMouseUp:", "otherMouseDragged:",
            "scrollWheel:", "keyDown:", "keyUp:", "flagsChanged:",
        };
        var methods = new List<(string, IntPtr, string)>
        {
            ("drawRect:", Callbacks.DrawRect, "v@:{CGRect={CGPoint=dd}{CGSize=dd}}"),
            (MacFrameClock.TickSelector, Callbacks.Event, ev), // 显示器同步节拍（CADisplayLink 目标 / CVDisplayLink 转发）
            ("acceptsFirstResponder", Callbacks.Bool, "c@:"),
            ("acceptsFirstMouse:", Callbacks.BoolArg, "c@:@"),
        };
        foreach (var e in viewEvents) methods.Add((e, Callbacks.Event, ev));
        // NSTextInputClient（输入法）：视图声明遵循该协议后 AppKit 为它提供 NSTextInputContext
        const string range = "{_NSRange=QQ}";
        methods.Add(("insertText:replacementRange:", Callbacks.InsertText, "v@:@" + range));
        methods.Add(("setMarkedText:selectedRange:replacementRange:", Callbacks.SetMarkedText, "v@:@" + range + range));
        methods.Add(("unmarkText", Callbacks.Void, "v@:"));
        methods.Add(("selectedRange", Callbacks.Range, range + "@:"));
        methods.Add(("markedRange", Callbacks.Range, range + "@:"));
        methods.Add(("hasMarkedText", Callbacks.Bool, "c@:"));
        methods.Add(("attributedSubstringForProposedRange:actualRange:", Callbacks.AttributedSubstring, "@@:" + range + "^" + range));
        methods.Add(("validAttributesForMarkedText", Callbacks.Object, "@@:"));
        methods.Add(("firstRectForCharacterRange:actualRange:", Callbacks.FirstRect, "{CGRect={CGPoint=dd}{CGSize=dd}}@:" + range + "^" + range));
        methods.Add(("characterIndexForPoint:", Callbacks.CharacterIndex, "Q@:{CGPoint=dd}"));
        methods.Add(("doCommandBySelector:", Callbacks.Event, "v@::"));
        DefineClass("AwAUIView", "NSView", methods, new[] { "NSTextInputClient" });

        // 无边框窗口（WindowDecorations.None）：NSWindow 对 Borderless 默认返回 NO，无法成为 key window；
        // 自绘标题栏的窗口需要键盘焦点与拖动，因此用一个允许 key / main 的子类。
        DefineClass("AwAUIWindow", "NSWindow", new (string, IntPtr, string)[]
        {
            ("canBecomeKeyWindow", Callbacks.Bool, "c@:"),
            ("canBecomeMainWindow", Callbacks.Bool, "c@:"),
        });

        DefineClass("AwAUIWindowDelegate", "NSObject", new (string, IntPtr, string)[]
        {
            ("windowShouldClose:", Callbacks.BoolArg, "c@:@"),
            ("windowWillClose:", Callbacks.Event, ev),
            ("windowDidBecomeKey:", Callbacks.Event, ev),
            ("windowDidResignKey:", Callbacks.Event, ev),
            ("windowDidChangeBackingProperties:", Callbacks.Event, ev),
            ("windowDidChangeOcclusionState:", Callbacks.Event, ev),
            ("windowDidChangeScreen:", Callbacks.Event, ev),
            // GPU 呈现（CAMetalLayer 宿主层）没有 drawRect:，尺寸变化与拖动缩放经这些通知处理
            ("windowDidResize:", Callbacks.Event, ev),
            ("windowWillStartLiveResize:", Callbacks.Event, ev),
            ("windowDidEndLiveResize:", Callbacks.Event, ev),
            // 窗口状态 / 位置：用户拖动、最小化、缩放与全屏切换
            ("windowDidMove:", Callbacks.Event, ev),
            ("windowDidMiniaturize:", Callbacks.Event, ev),
            ("windowDidDeminiaturize:", Callbacks.Event, ev),
            ("windowDidEnterFullScreen:", Callbacks.Event, ev),
            ("windowDidExitFullScreen:", Callbacks.Event, ev),
        });

        // 文件面板完成：-(void)awPanelDidEnd:(id)panel returnCode:(NSInteger)code contextInfo:(void *)ctx
        DefineClass("AwAUIStorageDelegate", "NSObject", new (string, IntPtr, string)[]
        {
            ("awPanelDidEnd:returnCode:contextInfo:", Callbacks.PanelEnd, "v@:@q^v"),
        });

        DefineClass("AwAUIHelper", "NSObject", new (string, IntPtr, string)[]
        {
            ("awDrain:", Callbacks.Event, ev),
            ("awQuit:", Callbacks.Event, ev),
            ("awAppDidBecomeActive:", Callbacks.Event, ev),
            ("awAppDidResignActive:", Callbacks.Event, ev),
            ("awScreensChanged:", Callbacks.Event, ev),
            (MacReducedMotionPreference.ChangedSelector, Callbacks.Event, ev),
            (MacAppearancePreference.ChangedSelector, Callbacks.Event, ev),
        });
    }

    MacWindow? Find(IntPtr handle) => _windowsByHandle.TryGetValue(handle, out var w) ? w : null;

    void HandleEvent(IntPtr self, IntPtr sel, IntPtr arg)
    {
        if (self == _helper)
        {
            if (sel == Sel("awDrain:")) _dispatcher.Drain();
            else if (sel == Sel("awQuit:")) Exit();
            else if (sel == Sel(MacReducedMotionPreference.ChangedSelector) && _reducedMotion is { } preference)
                _dispatcher.Post(preference.Refresh);
            else if (sel == Sel(MacAppearancePreference.ChangedSelector) && _appearance is { } appearance)
                _dispatcher.Post(appearance.Refresh);
            else if (sel == Sel("awAppDidBecomeActive:")) SetApplicationActive(true);
            else if (sel == Sel("awAppDidResignActive:")) SetApplicationActive(false);
            else if (sel == Sel("awScreensChanged:")) _screens?.OnScreensChanged();
            return;
        }
        Find(self)?.HandleEvent(sel, arg);
    }
}

/// <summary>主线程调度器：投递的操作经 performSelectorOnMainThread 在消息循环中执行。</summary>
internal sealed class MacDispatcher : IDispatcher
{
    readonly IntPtr _target;
    readonly Queue<Action> _queue = new();
    bool _scheduled;

    public MacDispatcher(IntPtr target) => _target = target;

    public bool CheckAccess() => SendByte(Class("NSThread"), Sel("isMainThread")) != 0;

    public void Post(Action action)
    {
        bool schedule;
        lock (_queue)
        {
            _queue.Enqueue(action);
            schedule = !_scheduled;
            _scheduled = true;
        }
        if (schedule)
            Send(_target, Sel("performSelectorOnMainThread:withObject:waitUntilDone:"), Sel("awDrain:"), IntPtr.Zero, 0);
    }

    public void Drain()
    {
        while (true)
        {
            Action a;
            lock (_queue)
            {
                if (_queue.Count == 0) { _scheduled = false; return; }
                a = _queue.Dequeue();
            }
            a();
        }
    }
}
