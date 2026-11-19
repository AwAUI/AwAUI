using System;

namespace AwAUI.Diagnostics.Json;

/// <summary>JSON 文本不合法。携带出错位置：字节偏移（0 起）、行与列（1 起，列按字节计）。</summary>
public sealed class JsonException : Exception
{
    public JsonException(string message, long bytePosition, int line, int column)
        : base($"{message} (line {line}, column {column}, byte {bytePosition})")
    {
        Reason = message;
        BytePosition = bytePosition;
        Line = line;
        Column = column;
    }

    /// <summary>不含位置信息的错误描述。</summary>
    public string Reason { get; }
    /// <summary>出错字节在输入中的偏移（0 起）。</summary>
    public long BytePosition { get; }
    /// <summary>出错位置所在行（1 起，以 \n 分行）。</summary>
    public int Line { get; }
    /// <summary>出错位置所在列（1 起，按字节计）。</summary>
    public int Column { get; }
}
