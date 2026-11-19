using System;
using AwAUI.Graphics.Commands;

namespace AwAUI.Graphics;

/// <summary>
/// DisplayList 的顺序读取器。后端解释器的典型用法：
/// <code>
/// var r = list.GetReader();
/// while (r.MoveNext()) switch (r.Op) { case DrawOp.FillRect: var c = r.Read&lt;FillRectCmd&gt;(); ... }
/// </code>
/// </summary>
public ref struct DisplayListReader
{
    readonly ReadOnlySpan<byte> _data;
    readonly object[] _resources;
    int _pos;
    int _payloadStart;
    int _payloadLength;

    internal DisplayListReader(ReadOnlySpan<byte> data, object[] resources)
    {
        _data = data;
        _resources = resources;
        _pos = 0;
        _payloadStart = 0;
        _payloadLength = 0;
        Op = DrawOp.None;
        Flags = DrawFlags.None;
        Index = -1;
    }

    public DrawOp Op { get; private set; }
    public DrawFlags Flags { get; private set; }

    /// <summary>当前命令的序号（从 0 开始）。</summary>
    public int Index { get; private set; }

    public unsafe bool MoveNext()
    {
        if (_pos + 4 > _data.Length) return false;
        CommandHeader h;
        fixed (byte* p = &_data[_pos]) h = *(CommandHeader*)p;
        int size = h.Size4 * 4;
        if (size < 4 || _pos + size > _data.Length)
            throw new InvalidOperationException($"Corrupt display list at offset {_pos}.");
        Op = h.Op;
        Flags = h.Flags;
        _payloadStart = _pos + 4;
        _payloadLength = size - 4;
        _pos += size;
        Index++;
        return true;
    }

    public ReadOnlySpan<byte> Payload => _data.Slice(_payloadStart, _payloadLength);

    public unsafe T Read<T>() where T : unmanaged
    {
        if (sizeof(T) > _payloadLength)
            throw new InvalidOperationException($"Payload of {Op} is {_payloadLength} bytes, {typeof(T).Name} needs {sizeof(T)}.");
        fixed (byte* p = &_data[_payloadStart]) return *(T*)p;
    }

    public T GetResource<T>(int index) where T : class =>
        (T)_resources[index];

    /// <summary>读取 <see cref="DrawOp.DrawGlyphRun"/> 命令引用的字形串。</summary>
    public Text.GlyphRun GetGlyphRun(in DrawGlyphRunCmd command) => (Text.GlyphRun)_resources[command.GlyphRunRef];

    /// <summary>读取 <see cref="DrawOp.DrawImage"/> 命令引用的位图。</summary>
    public Bitmap GetImage(in DrawImageCmd command) => (Bitmap)_resources[command.ImageRef];

    public object? GetBrush(in PaintData paint) => paint.BrushRef >= 0 ? _resources[paint.BrushRef] : null;
}
