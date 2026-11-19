using System;
using System.Threading.Tasks;
using AwAUI.Platform;
using AwAUI.X11.Interop;
using static AwAUI.X11.Interop.Xlib;

namespace AwAUI.X11;

/// <summary>
/// X11 剪贴板：CLIPBOARD 选择（ICCCM）。一个不可见的 InputOnly 窗口作为选择所有者与转换结果的接收者，协议的状态机在
/// <see cref="X11SelectionServer"/>（复制：回答其他程序的 SelectionRequest，大数据走 INCR）与 <see cref="X11SelectionReader"/>
/// （粘贴：XConvertSelection → SelectionNotify → 读属性，INCR 分块，UTF8_STRING 被拒绝时退回 STRING）。
/// 我们自己拥有 CLIPBOARD 时直接返回保存的文本。读取有超时（2 秒）与大小上限；只在平台线程上运行，其他线程的调用被投递过去。
/// </summary>
public sealed unsafe class X11Clipboard : Clipboard
{
    readonly X11Platform _platform;
    readonly XlibSelectionIO _io;
    readonly X11SelectionServer _server;
    readonly X11SelectionReader _reader;

    internal X11Clipboard(X11Platform platform)
    {
        _platform = platform;
        var d = platform.Display;
        var atoms = new X11SelectionAtoms(InternAtom(d, "CLIPBOARD"), InternAtom(d, "TARGETS"), InternAtom(d, "UTF8_STRING"),
            InternAtom(d, "text/plain;charset=utf-8"), XA_STRING, InternAtom(d, "TEXT"), InternAtom(d, "INCR"), XA_ATOM,
            InternAtom(d, "AWAUI_SELECTION"));
        _io = new XlibSelectionIO(platform) { Atoms = atoms };
        _server = new X11SelectionServer(_io, atoms);
        _reader = new X11SelectionReader(_io, atoms);
    }

    /// <summary>选择窗口的 XID。</summary>
    internal ulong Window => _io.Window;

    public override Task<string?> GetTextAsync()
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Run(() =>
        {
            if (_server.Text is { } own && XGetSelectionOwner(_platform.Display, _io.Atoms.Clipboard) == _io.Window)
            {
                tcs.TrySetResult(own);
                return;
            }
            _reader.Request(LibC.MonotonicTicks(), r => tcs.TrySetResult(r));
            XFlush(_platform.Display);
        }, tcs);
        return tcs.Task;
    }

    public override Task<bool> SetTextAsync(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Run(() =>
        {
            bool ok = _server.Own(LimitLength(text), _platform.LastEventTime);
            XFlush(_platform.Display);
            tcs.TrySetResult(ok);
        }, tcs);
        return tcs.Task;
    }

    void Run<T>(Action action, TaskCompletionSource<T> tcs)
    {
        void Guarded()
        {
            try { action(); }
            catch (Exception e) { tcs.TrySetException(e); }
        }
        if (_platform.Dispatcher.CheckAccess()) Guarded();
        else _platform.Dispatcher.Post(Guarded);
    }

    /// <summary>选择相关的事件（平台的事件循环先交给这里）。返回 true 表示已处理。</summary>
    internal bool HandleEvent(XEvent* ev)
    {
        long now = LibC.MonotonicTicks();
        switch (ev->type)
        {
            case SelectionRequest when ev->xselectionrequest.owner == _io.Window:
            {
                var r = ev->xselectionrequest;
                _server.OnSelectionRequest(r.requestor, r.selection, r.target, r.property, r.time, now);
                XFlush(_platform.Display);
                return true;
            }
            case SelectionClear when ev->xselectionclear.window == _io.Window:
                _server.OnSelectionClear(ev->xselectionclear.selection);
                return true;
            case SelectionNotify when ev->xselection.requestor == _io.Window:
            {
                var s = ev->xselection;
                _reader.OnSelectionNotify(s.selection, s.target, s.property, now);
                XFlush(_platform.Display);
                return true;
            }
            case PropertyNotify:
            {
                var p = ev->xproperty;
                if (p.window == _io.Window)
                {
                    if (p.state == PropertyNewValue) _reader.OnPropertyNewValue(p.window, p.atom, now);
                    return true;
                }
                if (p.state == PropertyDelete && _server.ActiveTransfers > 0)
                {
                    // 其他程序的请求者窗口：INCR 传输的下一块
                    _server.OnPropertyDeleted(p.window, p.atom, now);
                    XFlush(_platform.Display);
                    return true;
                }
                return false;
            }
        }
        return false;
    }

    internal long? NextDeadline
    {
        get
        {
            long? a = _server.NextDeadline, b = _reader.NextDeadline;
            if (a is null) return b;
            if (b is null) return a;
            return Math.Min(a.Value, b.Value);
        }
    }

    internal void CheckTimeouts(long now)
    {
        _server.CheckTimeouts(now);
        _reader.CheckTimeouts(now);
    }

    internal void Dispose() => _io.Dispose();
}

/// <summary><see cref="X11SelectionIO"/> 的 Xlib 实现。</summary>
internal sealed unsafe class XlibSelectionIO : X11SelectionIO
{
    readonly X11Platform _platform;
    readonly ulong _window;
    readonly int _maxChunk;

    public XlibSelectionIO(X11Platform platform)
    {
        _platform = platform;
        var d = platform.Display;
        var attributes = new XSetWindowAttributes { event_mask = PropertyChangeMask };
        // 不可见的 InputOnly 窗口：选择所有者，也接收转换结果（深度与视觉沿用父窗口）
        _window = XCreateWindow(d, platform.Root, -10, -10, 1, 1, 0, 0, InputOnly, IntPtr.Zero, CWEventMask, &attributes);
        if (_window == 0) throw new InvalidOperationException("XCreateWindow (clipboard) failed.");
        long max = XExtendedMaxRequestSize(d);
        if (max <= 0) max = XMaxRequestSize(d);
        // 请求上限以 4 字节为单位；留出请求头的余量，并限制单块大小以免一次传输阻塞事件循环太久
        _maxChunk = (int)Math.Max(4096, Math.Min(max * 4 - 1024, 1 << 18));
        Atoms = default;
    }

    public X11SelectionAtoms Atoms { get; set; }

    public override ulong Window => _window;

    public override int MaxChunkBytes => _maxChunk;

    IntPtr D => _platform.Display;

    public override bool GetProperty(ulong window, ulong property, bool delete, long maxBytes, out ulong type, out int format, out byte[] data, out long bytesAfter)
    {
        ulong actualType, nitems, after;
        int actualFormat;
        byte* prop = null;
        long length = Math.Max(1, (maxBytes + 3) / 4);
        int status = XGetWindowProperty(D, window, property, 0, length, delete ? 1 : 0, AnyPropertyType, &actualType, &actualFormat, &nitems, &after, &prop);
        type = 0;
        format = 0;
        data = Array.Empty<byte>();
        bytesAfter = 0;
        if (status != 0) return false;
        try
        {
            type = actualType;
            format = actualFormat;
            bytesAfter = (long)Math.Min(after, (ulong)long.MaxValue);
            // 32 位格式在 LP64 上每项占一个 long（8 字节），16 位格式每项一个 short
            long unit = actualFormat == 32 ? 8 : actualFormat == 16 ? 2 : 1;
            long bytes = (long)Math.Min(nitems, (ulong)int.MaxValue) * unit;
            if (bytes > maxBytes + 8) return false;
            if (prop != null && bytes > 0)
            {
                data = new byte[bytes];
                new ReadOnlySpan<byte>(prop, (int)bytes).CopyTo(data);
            }
            return true;
        }
        finally
        {
            if (prop != null) XFree(prop);
        }
    }

    public override void ChangeProperty(ulong window, ulong property, ulong type, byte[] data, int offset, int count)
    {
        fixed (byte* p = data) XChangeProperty(D, window, property, type, 8, PropModeReplace, p == null ? null : p + offset, count);
    }

    public override void ChangePropertyAtoms(ulong window, ulong property, ulong[] atoms)
    {
        var longs = stackalloc long[atoms.Length];
        for (int i = 0; i < atoms.Length; i++) longs[i] = (long)atoms[i];
        XChangeProperty(D, window, property, XA_ATOM, 32, PropModeReplace, (byte*)longs, atoms.Length);
    }

    public override void ChangePropertyIncr(ulong window, ulong property, long size)
    {
        long v = size;
        XChangeProperty(D, window, property, Atoms.Incr, 32, PropModeReplace, (byte*)&v, 1);
    }

    public override void SendSelectionNotify(ulong requestor, ulong selection, ulong target, ulong property, ulong time)
    {
        if (requestor == 0) return;
        XEvent ev = default;
        ev.xselection = new XSelectionEvent
        {
            type = SelectionNotify, send_event = 1, display = D, requestor = requestor, selection = selection, target = target,
            property = property, time = time,
        };
        XSendEvent(D, requestor, 0, 0, &ev);
    }

    public override void ConvertSelection(ulong selection, ulong target, ulong property, ulong time)
    {
        XDeleteProperty(D, _window, property);
        XConvertSelection(D, selection, target, property, _window, time == 0 ? _platform.LastEventTime : time);
    }

    public override void SelectPropertyChanges(ulong window, bool enable)
    {
        if (window == 0 || window == _window) return;
        XSelectInput(D, window, enable ? PropertyChangeMask : 0);
    }

    public override bool SetOwner(ulong selection, ulong time)
    {
        XSetSelectionOwner(D, selection, _window, time);
        return XGetSelectionOwner(D, selection) == _window;
    }

    public void Dispose()
    {
        if (_platform.Display != IntPtr.Zero) XDestroyWindow(_platform.Display, _window);
    }
}
