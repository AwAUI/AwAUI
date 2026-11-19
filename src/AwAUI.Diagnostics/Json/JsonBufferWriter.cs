using System;
using System.Buffers;

namespace AwAUI.Diagnostics.Json;

/// <summary>可增长的字节缓冲（<see cref="IBufferWriter{T}"/> 实现；netstandard2.0 没有 ArrayBufferWriter）。可 <see cref="Clear"/> 后复用。</summary>
public sealed class JsonBufferWriter : IBufferWriter<byte>
{
    byte[] _buffer;
    int _written;

    public JsonBufferWriter(int initialCapacity = 256)
    {
        if (initialCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(initialCapacity));
        _buffer = new byte[initialCapacity];
    }

    /// <summary>已写入的字节数。</summary>
    public int WrittenCount => _written;
    /// <summary>已写入的内容（下一次写入或 Clear 后失效）。</summary>
    public ReadOnlySpan<byte> WrittenSpan => new(_buffer, 0, _written);
    /// <summary>已写入的内容（下一次写入或 Clear 后失效）。</summary>
    public ReadOnlyMemory<byte> WrittenMemory => new(_buffer, 0, _written);

    /// <summary>复制出已写入的内容。</summary>
    public byte[] ToArray() => WrittenSpan.ToArray();

    /// <summary>清空内容，保留容量。</summary>
    public void Clear() => _written = 0;

    public void Advance(int count)
    {
        if (count < 0 || _written + count > _buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return new Memory<byte>(_buffer, _written, _buffer.Length - _written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return new Span<byte>(_buffer, _written, _buffer.Length - _written);
    }

    void Ensure(int sizeHint)
    {
        if (sizeHint < 1) sizeHint = 1;
        if (_buffer.Length - _written >= sizeHint) return;
        long want = Math.Max((long)_buffer.Length * 2, (long)_written + sizeHint);
        if (want > int.MaxValue) want = Math.Max((long)_written + sizeHint, int.MaxValue - 64);
        if (want > int.MaxValue) throw new OutOfMemoryException();
        Array.Resize(ref _buffer, (int)want);
    }
}
