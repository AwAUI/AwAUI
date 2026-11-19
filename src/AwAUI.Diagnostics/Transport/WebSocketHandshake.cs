using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace AwAUI.Diagnostics.Transport;

/// <summary>RFC 6455 §4.2 服务端握手：解析与校验 HTTP Upgrade 请求，计算 Sec-WebSocket-Accept。</summary>
internal static class WebSocketHandshake
{
    const string Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>Sec-WebSocket-Accept = Base64(SHA-1(key + GUID))。</summary>
    public static string ComputeAccept(string key)
    {
        using var sha1 = SHA1.Create();
        byte[] hash = sha1.ComputeHash(Encoding.ASCII.GetBytes(key + Guid));
        return Convert.ToBase64String(hash);
    }

    internal sealed class Request
    {
        public string Method = "";
        public string Target = "";
        public string Version = "";
        public readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);

        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
    }

    /// <summary>握手校验结果：成功时 <see cref="Status"/> 为 101。</summary>
    internal readonly struct Result
    {
        public Result(int status, string message, string? key = null)
        {
            Status = status; Message = message; Key = key;
        }
        public int Status { get; }
        public string Message { get; }
        public string? Key { get; }
        public bool Accepted => Status == 101;
    }

    /// <summary>解析请求头块（不含结尾的空行）。格式错误返回 null。</summary>
    public static Request? Parse(string head)
    {
        var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
        if (lines.Length == 0) return null;
        var parts = lines[0].Split(' ');
        if (parts.Length != 3) return null;
        var req = new Request { Method = parts[0], Target = parts[1], Version = parts[2] };
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0) continue;
            if (line[0] == ' ' || line[0] == '\t') return null; // 不支持过时的折行
            int colon = line.IndexOf(':');
            if (colon <= 0) return null;
            string name = line.Substring(0, colon);
            if (name.IndexOf(' ') >= 0 || name.IndexOf('\t') >= 0) return null;
            string value = line.Substring(colon + 1).Trim(' ', '\t');
            req.Headers[name] = req.Headers.TryGetValue(name, out var prev) ? prev + ", " + value : value;
        }
        return req;
    }

    public static Result Validate(Request? req, WebSocketServerOptions options)
    {
        if (req is null) return new Result(400, "Malformed HTTP request");
        if (req.Method != "GET") return new Result(405, "Method Not Allowed");
        if (!req.Version.StartsWith("HTTP/1.", StringComparison.Ordinal) || req.Version == "HTTP/1.0")
            return new Result(400, "HTTP/1.1 or later is required");
        if (options.Path != null)
        {
            string path = req.Target;
            int q = path.IndexOf('?');
            if (q >= 0) path = path.Substring(0, q);
            if (!string.Equals(path, options.Path, StringComparison.Ordinal)) return new Result(404, "Not Found");
        }
        if (req.Header("Host") is null) return new Result(400, "Missing Host header");
        if (!HasToken(req.Header("Upgrade"), "websocket")) return new Result(400, "Missing 'Upgrade: websocket'");
        if (!HasToken(req.Header("Connection"), "Upgrade")) return new Result(400, "Missing 'Connection: Upgrade'");
        if (!HasToken(req.Header("Sec-WebSocket-Version"), "13")) return new Result(426, "Unsupported WebSocket version");
        string? key = req.Header("Sec-WebSocket-Key");
        if (key is null || !IsValidKey(key)) return new Result(400, "Invalid Sec-WebSocket-Key");
        var validator = options.OriginValidator ?? WebSocketServerOptions.IsLocalOrigin;
        if (!validator(req.Header("Origin"))) return new Result(403, "Origin not allowed");
        return new Result(101, "Switching Protocols", key);
    }

    static bool IsValidKey(string key)
    {
        try { return Convert.FromBase64String(key).Length == 16; }
        catch (FormatException) { return false; }
    }

    /// <summary>逗号分隔的令牌列表中是否含有 <paramref name="token"/>（不区分大小写）。</summary>
    static bool HasToken(string? header, string token)
    {
        if (header is null) return false;
        foreach (var part in header.Split(','))
            if (string.Equals(part.Trim(' ', '\t'), token, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static byte[] BuildAcceptResponse(string key) => Encoding.ASCII.GetBytes(
        "HTTP/1.1 101 Switching Protocols\r\n" +
        "Upgrade: websocket\r\n" +
        "Connection: Upgrade\r\n" +
        "Sec-WebSocket-Accept: " + ComputeAccept(key) + "\r\n\r\n");

    public static byte[] BuildErrorResponse(int status, string message)
    {
        string reason = status switch
        {
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            426 => "Upgrade Required",
            431 => "Request Header Fields Too Large",
            503 => "Service Unavailable",
            _ => "Error",
        };
        var body = Encoding.UTF8.GetBytes(message);
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        if (status == 426) sb.Append("Sec-WebSocket-Version: 13\r\n");
        sb.Append("Content-Type: text/plain; charset=utf-8\r\n");
        sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");
        var head = Encoding.ASCII.GetBytes(sb.ToString());
        var all = new byte[head.Length + body.Length];
        Buffer.BlockCopy(head, 0, all, 0, head.Length);
        Buffer.BlockCopy(body, 0, all, head.Length, body.Length);
        return all;
    }
}
