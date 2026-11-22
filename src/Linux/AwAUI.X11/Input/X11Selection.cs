using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AwAUI.Platform;

namespace AwAUI.X11;

/// <summary>选择协议（ICCCM）用到的原子。</summary>
internal readonly struct X11SelectionAtoms
{
    public X11SelectionAtoms(ulong clipboard, ulong targets, ulong utf8String, ulong textPlainUtf8, ulong @string, ulong text,
        ulong incr, ulong atom, ulong property, ulong html = 0, ulong imagePng = 0, ulong uriList = 0)
    {
        Clipboard = clipboard; Targets = targets; Utf8String = utf8String; TextPlainUtf8 = textPlainUtf8; String = @string; Text = text;
        Incr = incr; Atom = atom; Property = property;
        Html = html; ImagePng = imagePng; UriList = uriList;
    }

    public ulong Clipboard { get; }
    public ulong Targets { get; }
    public ulong Utf8String { get; }

    /// <summary><c>text/plain;charset=utf-8</c>（部分 Wayland / 新式程序只认 MIME 名）。</summary>
    public ulong TextPlainUtf8 { get; }
    public ulong String { get; }
    public ulong Text { get; }
    public ulong Incr { get; }
    public ulong Atom { get; }

    /// <summary>我们接收转换结果用的属性名。</summary>
    public ulong Property { get; }

    /// <summary><c>text/html</c>；为 0 表示该测试 / 调用方不支持富格式。</summary>
    public ulong Html { get; }

    /// <summary><c>image/png</c>。</summary>
    public ulong ImagePng { get; }

    /// <summary><c>text/uri-list</c>。</summary>
    public ulong UriList { get; }
}

/// <summary>
/// 选择协议用到的 X 请求（由平台用 Xlib 实现；测试用内存中的假服务器实现）。
/// </summary>
internal abstract class X11SelectionIO
{
    /// <summary>我们的选择窗口（选择所有者与转换结果的接收者）。</summary>
    public abstract ulong Window { get; }

    /// <summary>单次 ChangeProperty 的最大字节数（超过时用 INCR 分块）。</summary>
    public abstract int MaxChunkBytes { get; }

    /// <summary>读取属性（<paramref name="delete"/> 时读后删除）；最多 <paramref name="maxBytes"/> 字节，<paramref name="bytesAfter"/> 为剩余字节。属性不存在时 type 为 0。</summary>
    public abstract bool GetProperty(ulong window, ulong property, bool delete, long maxBytes, out ulong type, out int format, out byte[] data, out long bytesAfter);

    /// <summary>8 位格式的属性（替换）。</summary>
    public abstract void ChangeProperty(ulong window, ulong property, ulong type, byte[] data, int offset, int count);

    /// <summary>ATOM 列表（32 位格式）。</summary>
    public abstract void ChangePropertyAtoms(ulong window, ulong property, ulong[] atoms);

    /// <summary>INCR 开始：类型 INCR、32 位格式、一个值（总字节数的下限）。</summary>
    public abstract void ChangePropertyIncr(ulong window, ulong property, long size);

    public abstract void SendSelectionNotify(ulong requestor, ulong selection, ulong target, ulong property, ulong time);

    public abstract void ConvertSelection(ulong selection, ulong target, ulong property, ulong time);

    /// <summary>开始 / 停止接收某窗口（其他程序的请求者窗口）的 PropertyNotify。</summary>
    public abstract void SelectPropertyChanges(ulong window, bool enable);

    /// <summary>成为选择的所有者；返回之后是否确实是所有者。</summary>
    public abstract bool SetOwner(ulong selection, ulong time);
}

/// <summary>我们拥有的剪贴板内容：纯文本、HTML、PNG、文件 URI 列表。任意一项可为 null。</summary>
internal sealed class X11ClipboardData
{
    public string? Text { get; set; }
    public string? Html { get; set; }
    public byte[]? Png { get; set; }
    public string? UriList { get; set; }

    public bool IsEmpty => Text is null && Html is null && Png is null && UriList is null;
}

/// <summary>
/// CLIPBOARD 选择的所有者一方：保存我们复制的内容，回答其他程序的 SelectionRequest（TARGETS、UTF8_STRING、
/// text/plain;charset=utf-8、TEXT、STRING，以及可用的 text/html、image/png、text/uri-list）；
/// 超过单次请求上限的数据按 INCR 协议分块（请求者每删除一次属性发下一块，最后发零长度块）。
/// 同时进行的 INCR 传输数与每次等待都有上限，超时的传输被丢弃（请求者不再响应时不会无限占用内存）。
/// </summary>
internal sealed class X11SelectionServer
{
    public const int MaxTransfers = 16;
    public static readonly long TransferTimeoutTicks = TimeSpan.FromSeconds(5).Ticks;

    readonly X11SelectionIO _io;
    readonly X11SelectionAtoms _a;
    readonly List<Transfer> _transfers = new();
    X11ClipboardData? _data;

    sealed class Transfer
    {
        public ulong Requestor, Property, Type;
        public byte[] Data = Array.Empty<byte>();
        public int Offset;
        public long Deadline;
    }

    public X11SelectionServer(X11SelectionIO io, X11SelectionAtoms atoms)
    {
        _io = io;
        _a = atoms;
    }

    /// <summary>我们拥有 CLIPBOARD 时的文本；否则为 null。</summary>
    public string? Text => _data?.Text;

    /// <summary>我们拥有 CLIPBOARD 时的 HTML；否则为 null。</summary>
    public string? Html => _data?.Html;

    /// <summary>我们拥有 CLIPBOARD 时的 PNG 字节；否则为 null。</summary>
    public byte[]? Png => _data?.Png;

    /// <summary>我们拥有 CLIPBOARD 时的 <c>text/uri-list</c>；否则为 null。</summary>
    public string? UriList => _data?.UriList;

    public int ActiveTransfers => _transfers.Count;

    /// <summary>复制纯文本：成为 CLIPBOARD 的所有者。失败（别的程序在更晚的时间戳上抢到）时返回 false。</summary>
    public bool Own(string text, ulong time) => Own(new X11ClipboardData { Text = text }, time);

    /// <summary>复制一组格式：成为 CLIPBOARD 的所有者。失败时返回 false 并清空内容。</summary>
    public bool Own(X11ClipboardData data, ulong time)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));
        _transfers.Clear();
        if (!_io.SetOwner(_a.Clipboard, time))
        {
            _data = null;
            return false;
        }
        _data = data;
        return true;
    }

    /// <summary>SelectionClear：别的程序成为了所有者。</summary>
    public void OnSelectionClear(ulong selection)
    {
        if (selection != _a.Clipboard) return;
        _data = null;
        foreach (var t in _transfers) _io.SelectPropertyChanges(t.Requestor, false);
        _transfers.Clear();
    }

    public void OnSelectionRequest(ulong requestor, ulong selection, ulong target, ulong property, ulong time, long now)
    {
        if (property == 0) property = target; // 过时的客户端（ICCCM 2.2）
        var content = _data;
        if (selection != _a.Clipboard || content is null || content.IsEmpty || requestor == 0)
        {
            _io.SendSelectionNotify(requestor, selection, target, 0, time);
            return;
        }
        if (target == _a.Targets)
        {
            _io.ChangePropertyAtoms(requestor, property, AvailableTargets(content));
            _io.SendSelectionNotify(requestor, selection, target, property, time);
            return;
        }
        byte[] data;
        ulong type;
        string? text = content.Text;
        if (target == _a.Utf8String || target == _a.TextPlainUtf8)
        {
            if (text is null) { Reject(requestor, selection, target, time); return; }
            data = Encoding.UTF8.GetBytes(text);
            type = target;
        }
        else if (target == _a.Text)
        {
            if (text is null) { Reject(requestor, selection, target, time); return; }
            data = Encoding.UTF8.GetBytes(text);
            type = _a.Utf8String; // TEXT 由所有者选择编码
        }
        else if (target == _a.String)
        {
            if (text is null) { Reject(requestor, selection, target, time); return; }
            data = ToLatin1(text);
            type = _a.String;
        }
        else if (_a.Html != 0 && target == _a.Html && content.Html is string html)
        {
            data = Encoding.UTF8.GetBytes(html);
            type = _a.Html;
        }
        else if (_a.ImagePng != 0 && target == _a.ImagePng && content.Png is byte[] png)
        {
            data = png;
            type = _a.ImagePng;
        }
        else if (_a.UriList != 0 && target == _a.UriList && content.UriList is string uriList)
        {
            data = Encoding.UTF8.GetBytes(uriList);
            type = _a.UriList;
        }
        else
        {
            Reject(requestor, selection, target, time);
            return;
        }
        if (data.Length <= _io.MaxChunkBytes)
        {
            _io.ChangeProperty(requestor, property, type, data, 0, data.Length);
            _io.SendSelectionNotify(requestor, selection, target, property, time);
            return;
        }
        if (_transfers.Count >= MaxTransfers)
        {
            Reject(requestor, selection, target, time);
            return;
        }
        // INCR：先声明大小，请求者删除属性后逐块发送
        _io.SelectPropertyChanges(requestor, true);
        _io.ChangePropertyIncr(requestor, property, data.Length);
        _io.SendSelectionNotify(requestor, selection, target, property, time);
        _transfers.Add(new Transfer { Requestor = requestor, Property = property, Type = type, Data = data, Offset = 0, Deadline = now + TransferTimeoutTicks });
    }

    void Reject(ulong requestor, ulong selection, ulong target, ulong time) =>
        _io.SendSelectionNotify(requestor, selection, target, 0, time);

    ulong[] AvailableTargets(X11ClipboardData content)
    {
        var targets = new List<ulong>(8) { _a.Targets };
        if (content.Text is not null)
        {
            targets.Add(_a.Utf8String);
            targets.Add(_a.TextPlainUtf8);
            targets.Add(_a.Text);
            targets.Add(_a.String);
        }
        if (content.Html is not null && _a.Html != 0) targets.Add(_a.Html);
        if (content.Png is not null && _a.ImagePng != 0) targets.Add(_a.ImagePng);
        if (content.UriList is not null && _a.UriList != 0) targets.Add(_a.UriList);
        return targets.ToArray();
    }

    /// <summary>请求者删除了属性（PropertyNotify，state = PropertyDelete）：发下一块；全部发完后发零长度块结束。</summary>
    public void OnPropertyDeleted(ulong window, ulong property, long now)
    {
        for (int i = 0; i < _transfers.Count; i++)
        {
            var t = _transfers[i];
            if (t.Requestor != window || t.Property != property) continue;
            int chunk = Math.Min(_io.MaxChunkBytes, t.Data.Length - t.Offset);
            _io.ChangeProperty(window, property, t.Type, t.Data, t.Offset, chunk);
            t.Offset += chunk;
            t.Deadline = now + TransferTimeoutTicks;
            if (chunk == 0)
            {
                _transfers.RemoveAt(i);
                _io.SelectPropertyChanges(window, false);
            }
            return;
        }
    }

    /// <summary>丢弃超时的 INCR 传输。</summary>
    public void CheckTimeouts(long now)
    {
        for (int i = _transfers.Count - 1; i >= 0; i--)
        {
            if (now < _transfers[i].Deadline) continue;
            _io.SelectPropertyChanges(_transfers[i].Requestor, false);
            _transfers.RemoveAt(i);
        }
    }

    public long? NextDeadline
    {
        get
        {
            long? d = null;
            foreach (var t in _transfers) d = d is null ? t.Deadline : Math.Min(d.Value, t.Deadline);
            return d;
        }
    }

    static byte[] ToLatin1(string s)
    {
        var bytes = new byte[s.Length];
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(s[i - 1])) continue;
            bytes[n++] = c <= 0xFF ? (byte)c : (byte)'?';
        }
        Array.Resize(ref bytes, n);
        return bytes;
    }
}

/// <summary>
/// CLIPBOARD 选择的请求者一方（粘贴）：XConvertSelection → SelectionNotify → 读取属性；文本请求在 UTF8_STRING
/// 被拒绝时退回 STRING。所有者用 INCR 分块时，每次 PropertyNotify(NewValue) 读取并删除一块，零长度块结束。
/// 请求按顺序处理（同一时刻只有一个转换在途），多个文本请求合并为一个转换；也支持读取任意目标（HTML、PNG、
/// text/uri-list）的字节结果。每个请求的总大小（<see cref="MaxBytes"/> 或调用方给出的上限）与每一步的等待
/// （<see cref="TimeoutTicks"/>）都有上限：对方不响应或数据过大时以 null 结束，并继续处理队列。
/// </summary>
internal sealed class X11SelectionReader
{
    public const long MaxBytes = 4L * Clipboard.MaxTextLength;
    public static readonly long TimeoutTicks = TimeSpan.FromSeconds(2).Ticks;

    enum State : byte { Idle, WaitingNotify, Incremental }

    sealed class Pending
    {
        public ulong Target;
        public ulong FallbackTarget;
        public long MaxBytes;
        public bool Text;
        public List<Action<string?>>? TextWaiters;
        public Action<byte[]?>? BytesDone;
        public bool FallbackTried;
    }

    readonly X11SelectionIO _io;
    readonly X11SelectionAtoms _a;
    readonly Queue<Pending> _queue = new();
    Pending? _current;
    State _state;
    ulong _incrType;
    long _deadline;
    readonly MemoryStream _buffer = new();

    public X11SelectionReader(X11SelectionIO io, X11SelectionAtoms atoms)
    {
        _io = io;
        _a = atoms;
    }

    public bool Busy => _state != State.Idle;

    public long? NextDeadline => _state == State.Idle ? null : _deadline;

    /// <summary>请求读取剪贴板文本；进行中或排队中的文本请求会合并（所有等待者得到同一个结果）。</summary>
    public void Request(long now, Action<string?> done)
    {
        if (done is null) throw new ArgumentNullException(nameof(done));
        if (_current is { Text: true } current && current.Target == _a.Utf8String)
        {
            current.TextWaiters!.Add(done);
            return;
        }
        foreach (var queued in _queue)
        {
            if (!queued.Text) continue;
            queued.TextWaiters!.Add(done);
            return;
        }
        var request = new Pending
        {
            Target = _a.Utf8String,
            FallbackTarget = _a.String,
            MaxBytes = MaxBytes,
            Text = true,
            TextWaiters = new List<Action<string?>> { done },
        };
        _queue.Enqueue(request);
        StartNext(now);
    }

    /// <summary>请求读取任意目标（例如 text/html、image/png）；结果按原字节返回，不支持的编码由调用方处理。</summary>
    public void RequestBytes(ulong target, long maxBytes, long now, Action<byte[]?> done)
    {
        if (done is null) throw new ArgumentNullException(nameof(done));
        if (maxBytes <= 0)
        {
            done(null);
            return;
        }
        _queue.Enqueue(new Pending { Target = target, MaxBytes = maxBytes, BytesDone = done });
        StartNext(now);
    }

    void StartNext(long now)
    {
        while (_state == State.Idle && _queue.Count > 0)
        {
            _current = _queue.Dequeue();
            Begin(_current.Target, now);
        }
    }

    void Begin(ulong target, long now)
    {
        _state = State.WaitingNotify;
        _deadline = now + TimeoutTicks;
        _incrType = 0;
        _buffer.SetLength(0);
        _io.ConvertSelection(_a.Clipboard, target, _a.Property, 0);
    }

    public void OnSelectionNotify(ulong selection, ulong target, ulong property, long now)
    {
        if (_state != State.WaitingNotify || selection != _a.Clipboard) return;
        var request = _current;
        if (request is null) return;
        if (property == 0)
        {
            if (request.Text && !request.FallbackTried && request.FallbackTarget != 0)
            {
                request.FallbackTried = true;
                Begin(request.FallbackTarget, now);
                return;
            }
            Complete(null, 0, now);
            return;
        }
        if (!_io.GetProperty(_io.Window, property, true, request.MaxBytes, out var type, out _, out var data, out long after) || type == 0)
        {
            Complete(null, 0, now);
            return;
        }
        if (type == _a.Incr)
        {
            // 删除属性（上面已经删除）通知所有者开始发送分块
            _state = State.Incremental;
            _incrType = 0;
            _buffer.SetLength(0);
            _deadline = now + TimeoutTicks;
            return;
        }
        Complete(after > 0 ? null : data, after > 0 ? 0 : type, now);
    }

    public void OnPropertyNewValue(ulong window, ulong property, long now)
    {
        if (_state != State.Incremental || window != _io.Window || property != _a.Property) return;
        var request = _current;
        if (request is null) return;
        if (!_io.GetProperty(window, property, true, request.MaxBytes, out var type, out _, out var data, out _))
        {
            Complete(null, 0, now);
            return;
        }
        if (data.Length == 0)
        {
            Complete(_buffer.ToArray(), _incrType, now);
            return;
        }
        if (_incrType == 0) _incrType = type;
        if (_buffer.Length + data.Length > request.MaxBytes)
        {
            Complete(null, 0, now);
            return;
        }
        _buffer.Write(data, 0, data.Length);
        _deadline = now + TimeoutTicks;
    }

    public void CheckTimeouts(long now)
    {
        if (_state != State.Idle && now >= _deadline) Complete(null, 0, now);
    }

    void Complete(byte[]? data, ulong type, long now)
    {
        var request = _current;
        _current = null;
        _state = State.Idle;
        _incrType = 0;
        _buffer.SetLength(0);
        if (_buffer.Capacity > 1 << 20) _buffer.Capacity = 0;
        // 先启动队列里的下一个请求，回调里再次 Request 时会并入新请求而不是并发出两个转换
        StartNext(now);
        if (request is null) return;
        if (request.Text)
        {
            string? text = DecodeText(type, data);
            foreach (var waiter in request.TextWaiters!) waiter(text);
        }
        else request.BytesDone?.Invoke(data);
    }

    string? DecodeText(ulong type, byte[]? data)
    {
        if (data is null) return null;
        string text;
        if (type == _a.Utf8String || type == _a.TextPlainUtf8) text = Encoding.UTF8.GetString(data);
        else if (type == _a.String)
        {
            var chars = new char[data.Length];
            for (int i = 0; i < data.Length; i++) chars[i] = (char)data[i];
            text = new string(chars);
        }
        else return null;
        text = Clipboard.LimitLength(Clipboard.NormalizeNewlines(text));
        return text.Length == 0 ? null : text;
    }
}
