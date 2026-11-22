using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using AwAUI.Graphics;
using AwAUI.Platform;
using AwAUI.X11.Interop;
using static AwAUI.X11.Interop.Xlib;

namespace AwAUI.X11;

/// <summary>
/// X11 剪贴板：CLIPBOARD 选择（ICCCM）。一个不可见的 InputOnly 窗口作为选择所有者与转换结果的接收者，协议的状态机在
/// <see cref="X11SelectionServer"/>（复制：回答其他程序的 SelectionRequest，大数据走 INCR）与 <see cref="X11SelectionReader"/>
/// （粘贴：XConvertSelection → SelectionNotify → 读属性，INCR 分块，UTF8_STRING 被拒绝时退回 STRING）。
/// 支持纯文本、<c>text/html</c>、<c>image/png</c> 与 <c>text/uri-list</c>；我们自己拥有 CLIPBOARD 时直接返回保存的内容。
/// 读取有超时（2 秒）与大小上限；只在平台线程上运行，其他线程的调用被投递过去。
/// </summary>
public sealed unsafe class X11Clipboard : Clipboard
{
    /// <summary>读取 PNG 图片的字节上限（32 MB；解码后的像素另有上限）。</summary>
    public const int MaxImageBytes = 32 * 1024 * 1024;

    /// <summary>读取 HTML 的字节上限（4 字节/码元 × <see cref="Clipboard.MaxTextLength"/>）。</summary>
    public const long MaxHtmlBytes = 4L * MaxTextLength;

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
            InternAtom(d, "AWAUI_SELECTION"), InternAtom(d, "text/html"), InternAtom(d, "image/png"),
            InternAtom(d, "text/uri-list"));
        _io = new XlibSelectionIO(platform) { Atoms = atoms };
        _server = new X11SelectionServer(_io, atoms);
        _reader = new X11SelectionReader(_io, atoms);
    }

    /// <summary>选择窗口的 XID。</summary>
    internal ulong Window => _io.Window;

    bool Owns() => XGetSelectionOwner(_platform.Display, _io.Atoms.Clipboard) == _io.Window;

    public override Task<string?> GetTextAsync()
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Run(() =>
        {
            if (Owns())
            {
                tcs.TrySetResult(_server.Text);
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
            bool ok = _server.Own(new X11ClipboardData { Text = LimitLength(text) }, _platform.LastEventTime);
            XFlush(_platform.Display);
            tcs.TrySetResult(ok);
        }, tcs);
        return tcs.Task;
    }

    public override Task<string?> GetHtmlAsync()
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Run(() =>
        {
            if (Owns())
            {
                tcs.TrySetResult(_server.Html);
                return;
            }
            _reader.RequestBytes(_io.Atoms.Html, MaxHtmlBytes, LibC.MonotonicTicks(), data =>
            {
                if (data is null || data.Length == 0) { tcs.TrySetResult(null); return; }
                tcs.TrySetResult(LimitLength(Encoding.UTF8.GetString(data)));
            });
            XFlush(_platform.Display);
        }, tcs);
        return tcs.Task;
    }

    public override Task<bool> SetHtmlAsync(string html)
    {
        if (html is null) throw new ArgumentNullException(nameof(html));
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Run(() =>
        {
            string limited = LimitLength(html);
            string plain = X11ClipboardFormats.HtmlToPlainText(limited);
            var data = new X11ClipboardData
            {
                Html = limited,
                Text = plain.Length > 0 ? LimitLength(plain) : null,
            };
            bool ok = _server.Own(data, _platform.LastEventTime);
            XFlush(_platform.Display);
            tcs.TrySetResult(ok);
        }, tcs);
        return tcs.Task;
    }

    public override Task<Bitmap?> GetImageAsync()
    {
        var tcs = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Run(() =>
        {
            if (Owns())
            {
                tcs.TrySetResult(DecodePng(_server.Png));
                return;
            }
            _reader.RequestBytes(_io.Atoms.ImagePng, MaxImageBytes, LibC.MonotonicTicks(), data =>
                tcs.TrySetResult(DecodePng(data)));
            XFlush(_platform.Display);
        }, tcs);
        return tcs.Task;
    }

    public override Task<bool> SetImageAsync(Bitmap image)
    {
        if (image is null) throw new ArgumentNullException(nameof(image));
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Run(() =>
        {
            if ((long)image.Width * image.Height > X11Png.MaxPixels)
            {
                tcs.TrySetResult(false);
                return;
            }
            byte[] png;
            try { png = X11Png.Encode(image); }
            catch (Exception e) when (e is ArgumentException or OutOfMemoryException)
            {
                tcs.TrySetResult(false);
                return;
            }
            bool ok = _server.Own(new X11ClipboardData { Png = png }, _platform.LastEventTime);
            XFlush(_platform.Display);
            tcs.TrySetResult(ok);
        }, tcs);
        return tcs.Task;
    }

    public override Task<IReadOnlyList<StorageFile>> GetFilesAsync()
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<StorageFile>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Run(() =>
        {
            if (Owns())
            {
                tcs.TrySetResult(FilesFromUriList(_server.UriList));
                return;
            }
            _reader.RequestBytes(_io.Atoms.UriList, X11ClipboardFormats.MaxUriListLength, LibC.MonotonicTicks(), data =>
                tcs.TrySetResult(FilesFromUriList(data is null ? null : Encoding.UTF8.GetString(data))));
            XFlush(_platform.Display);
        }, tcs);
        return tcs.Task;
    }

    public override Task<bool> SetFilesAsync(IReadOnlyList<StorageFile> files)
    {
        if (files is null) throw new ArgumentNullException(nameof(files));
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Run(() =>
        {
            var paths = new List<string>(Math.Min(files.Count, X11ClipboardFormats.MaxUriListItems));
            foreach (var file in files)
            {
                if (paths.Count >= X11ClipboardFormats.MaxUriListItems) break;
                string? path = file?.Path;
                if (path is not null) paths.Add(path);
            }
            string? uriList = X11ClipboardFormats.BuildUriList(paths);
            if (uriList is null)
            {
                tcs.TrySetResult(false);
                return;
            }
            string? text = X11ClipboardFormats.BuildTextFileList(paths);
            bool ok = _server.Own(new X11ClipboardData { UriList = uriList, Text = text }, _platform.LastEventTime);
            XFlush(_platform.Display);
            tcs.TrySetResult(ok);
        }, tcs);
        return tcs.Task;
    }

    static Bitmap? DecodePng(byte[]? png)
    {
        if (png is null || png.Length == 0) return null;
        try { return X11Png.Decode(png); }
        catch (Exception e) when (e is System.IO.InvalidDataException or NotSupportedException or ArgumentException or OutOfMemoryException)
        {
            return null;
        }
    }

    static IReadOnlyList<StorageFile> FilesFromUriList(string? text)
    {
        var paths = X11ClipboardFormats.ParseUriList(text);
        if (paths.Count == 0) return Array.Empty<StorageFile>();
        var files = new List<StorageFile>(paths.Count);
        foreach (var path in paths)
        {
            try
            {
                if (System.IO.File.Exists(path)) files.Add(new X11StorageFile(path));
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }
        return files;
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
