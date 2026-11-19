using System;
using System.Collections.Generic;
using System.Text;
using AwAUI.Platform;

namespace AwAUI.X11;

/// <summary>选择协议（ICCCM）用到的原子。</summary>
internal readonly struct X11SelectionAtoms
{
    public X11SelectionAtoms(ulong clipboard, ulong targets, ulong utf8String, ulong textPlainUtf8, ulong @string, ulong text,
        ulong incr, ulong atom, ulong property)
    {
        Clipboard = clipboard; Targets = targets; Utf8String = utf8String; TextPlainUtf8 = textPlainUtf8; String = @string; Text = text;
        Incr = incr; Atom = atom; Property = property;
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

/// <summary>
/// CLIPBOARD 选择的所有者一方：保存我们复制的文本，回答其他程序的 SelectionRequest（TARGETS、UTF8_STRING、text/plain;charset=utf-8、
/// TEXT、STRING）；超过单次请求上限的数据按 INCR 协议分块（请求者每删除一次属性发下一块，最后发零长度块）。
/// 同时进行的 INCR 传输数与每次等待都有上限，超时的传输被丢弃（请求者不再响应时不会无限占用内存）。
/// </summary>
internal sealed class X11SelectionServer
{
    public const int MaxTransfers = 16;
    public static readonly long TransferTimeoutTicks = TimeSpan.FromSeconds(5).Ticks;

    readonly X11SelectionIO _io;
    readonly X11SelectionAtoms _a;
    readonly List<Transfer> _transfers = new();
    string? _text;
    byte[]? _utf8;

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
    public string? Text => _text;

    public int ActiveTransfers => _transfers.Count;

    /// <summary>复制：成为 CLIPBOARD 的所有者。失败（别的程序在更晚的时间戳上抢到）时返回 false。</summary>
    public bool Own(string text, ulong time)
    {
        _transfers.Clear();
        if (!_io.SetOwner(_a.Clipboard, time))
        {
            _text = null;
            _utf8 = null;
            return false;
        }
        _text = text;
        _utf8 = Encoding.UTF8.GetBytes(text);
        return true;
    }

    /// <summary>SelectionClear：别的程序成为了所有者。</summary>
    public void OnSelectionClear(ulong selection)
    {
        if (selection != _a.Clipboard) return;
        _text = null;
        _utf8 = null;
        foreach (var t in _transfers) _io.SelectPropertyChanges(t.Requestor, false);
        _transfers.Clear();
    }

    public void OnSelectionRequest(ulong requestor, ulong selection, ulong target, ulong property, ulong time, long now)
    {
        if (property == 0) property = target; // 过时的客户端（ICCCM 2.2）
        if (selection != _a.Clipboard || _utf8 is null || requestor == 0)
        {
            _io.SendSelectionNotify(requestor, selection, target, 0, time);
            return;
        }
        if (target == _a.Targets)
        {
            _io.ChangePropertyAtoms(requestor, property, new[] { _a.Targets, _a.Utf8String, _a.TextPlainUtf8, _a.Text, _a.String });
            _io.SendSelectionNotify(requestor, selection, target, property, time);
            return;
        }
        byte[] data;
        ulong type;
        if (target == _a.Utf8String || target == _a.TextPlainUtf8)
        {
            data = _utf8;
            type = target;
        }
        else if (target == _a.Text)
        {
            data = _utf8;
            type = _a.Utf8String; // TEXT 由所有者选择编码
        }
        else if (target == _a.String)
        {
            data = ToLatin1(_text!);
            type = _a.String;
        }
        else
        {
            _io.SendSelectionNotify(requestor, selection, target, 0, time);
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
            _io.SendSelectionNotify(requestor, selection, target, 0, time);
            return;
        }
        // INCR：先声明大小，请求者删除属性后逐块发送
        _io.SelectPropertyChanges(requestor, true);
        _io.ChangePropertyIncr(requestor, property, data.Length);
        _io.SendSelectionNotify(requestor, selection, target, property, time);
        _transfers.Add(new Transfer { Requestor = requestor, Property = property, Type = type, Data = data, Offset = 0, Deadline = now + TransferTimeoutTicks });
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
/// CLIPBOARD 选择的请求者一方（粘贴）：XConvertSelection(UTF8_STRING) → SelectionNotify → 读取属性；UTF8_STRING 被拒绝时退回 STRING；
/// 所有者用 INCR 分块时，每次 PropertyNotify(NewValue) 读取并删除一块，零长度块结束。
/// 总大小（<see cref="MaxBytes"/>）与每一步的等待（<see cref="TimeoutTicks"/>）都有上限：对方不响应或数据过大时以 null 结束。
/// </summary>
internal sealed class X11SelectionReader
{
    public const long MaxBytes = 4L * Clipboard.MaxTextLength;
    public static readonly long TimeoutTicks = TimeSpan.FromSeconds(2).Ticks;

    enum State : byte { Idle, WaitingNotify, Incremental }

    readonly X11SelectionIO _io;
    readonly X11SelectionAtoms _a;
    readonly List<Action<string?>> _waiters = new();
    State _state;
    ulong _target;
    ulong _incrType;
    long _deadline;
    readonly System.IO.MemoryStream _buffer = new();

    public X11SelectionReader(X11SelectionIO io, X11SelectionAtoms atoms)
    {
        _io = io;
        _a = atoms;
    }

    public bool Busy => _state != State.Idle;

    public long? NextDeadline => _state == State.Idle ? null : _deadline;

    /// <summary>请求读取剪贴板文本；进行中的请求会合并（所有等待者得到同一个结果）。</summary>
    public void Request(long now, Action<string?> done)
    {
        _waiters.Add(done);
        if (_state != State.Idle) return;
        Begin(_a.Utf8String, now);
    }

    void Begin(ulong target, long now)
    {
        _target = target;
        _state = State.WaitingNotify;
        _deadline = now + TimeoutTicks;
        _io.ConvertSelection(_a.Clipboard, target, _a.Property, 0);
    }

    public void OnSelectionNotify(ulong selection, ulong target, ulong property, long now)
    {
        if (_state != State.WaitingNotify || selection != _a.Clipboard) return;
        if (property == 0)
        {
            if (_target == _a.Utf8String) Begin(_a.String, now);
            else Finish(null);
            return;
        }
        if (!_io.GetProperty(_io.Window, property, true, MaxBytes, out var type, out _, out var data, out long after) || type == 0)
        {
            Finish(null);
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
        Finish(after > 0 ? null : Decode(type, data, data.Length));
    }

    public void OnPropertyNewValue(ulong window, ulong property, long now)
    {
        if (_state != State.Incremental || window != _io.Window || property != _a.Property) return;
        if (!_io.GetProperty(window, property, true, MaxBytes, out var type, out _, out var data, out _))
        {
            Finish(null);
            return;
        }
        if (data.Length == 0)
        {
            Finish(Decode(_incrType, _buffer.GetBuffer(), (int)_buffer.Length));
            return;
        }
        if (_incrType == 0) _incrType = type;
        if (_buffer.Length + data.Length > MaxBytes)
        {
            Finish(null);
            return;
        }
        _buffer.Write(data, 0, data.Length);
        _deadline = now + TimeoutTicks;
    }

    public void CheckTimeouts(long now)
    {
        if (_state != State.Idle && now >= _deadline) Finish(null);
    }

    void Finish(string? result)
    {
        _state = State.Idle;
        _buffer.SetLength(0);
        if (_buffer.Capacity > 1 << 20) _buffer.Capacity = 0;
        var waiters = _waiters.ToArray();
        _waiters.Clear();
        foreach (var w in waiters) w(result);
    }

    string? Decode(ulong type, byte[] data, int length)
    {
        string text;
        if (type == _a.Utf8String || type == _a.TextPlainUtf8) text = Encoding.UTF8.GetString(data, 0, length);
        else if (type == _a.String)
        {
            var chars = new char[length];
            for (int i = 0; i < length; i++) chars[i] = (char)data[i];
            text = new string(chars);
        }
        else return null;
        text = Clipboard.LimitLength(Clipboard.NormalizeNewlines(text));
        return text.Length == 0 ? null : text;
    }
}
