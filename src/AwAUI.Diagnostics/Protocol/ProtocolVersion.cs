using System;
using System.Globalization;

namespace AwAUI.Diagnostics.Protocol;

/// <summary>
/// 协议版本「主.次」（design/20 会话）。主版本不同拒绝连接；次版本取双方较小者，
/// 新增于更高次版本的方法在协商出的旧版本下返回「方法不存在」。
/// </summary>
public readonly struct ProtocolVersion : IEquatable<ProtocolVersion>, IComparable<ProtocolVersion>
{
    public ProtocolVersion(int major, int minor)
    {
        if (major < 0) throw new ArgumentOutOfRangeException(nameof(major));
        if (minor < 0) throw new ArgumentOutOfRangeException(nameof(minor));
        Major = major; Minor = minor;
    }

    public int Major { get; }
    public int Minor { get; }

    /// <summary>与对端版本协商：主版本必须相同，次版本取较小者。</summary>
    public bool TryNegotiate(ProtocolVersion peer, out ProtocolVersion negotiated)
    {
        if (peer.Major != Major) { negotiated = default; return false; }
        negotiated = new ProtocolVersion(Major, Math.Min(Minor, peer.Minor));
        return true;
    }

    /// <summary>解析 "主.次"，两部分都必须是十进制非负整数。</summary>
    public static bool TryParse(string? text, out ProtocolVersion version)
    {
        version = default;
        if (string.IsNullOrEmpty(text)) return false;
        int dot = text!.IndexOf('.');
        if (dot <= 0 || dot == text.Length - 1 || text.IndexOf('.', dot + 1) >= 0) return false;
        if (!TryParsePart(text.Substring(0, dot), out int major) || !TryParsePart(text.Substring(dot + 1), out int minor)) return false;
        version = new ProtocolVersion(major, minor);
        return true;
    }

    static bool TryParsePart(string s, out int value)
    {
        value = 0;
        foreach (char c in s) if (c < '0' || c > '9') return false;
        return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    public static ProtocolVersion Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"Invalid protocol version '{text}'.");

    public override string ToString() => Major.ToString(CultureInfo.InvariantCulture) + "." + Minor.ToString(CultureInfo.InvariantCulture);

    public bool Equals(ProtocolVersion other) => Major == other.Major && Minor == other.Minor;
    public override bool Equals(object? obj) => obj is ProtocolVersion v && Equals(v);
    public override int GetHashCode() => HashCodeCompat.Combine(Major, Minor);
    public int CompareTo(ProtocolVersion other) => Major != other.Major ? Major.CompareTo(other.Major) : Minor.CompareTo(other.Minor);
    public static bool operator ==(ProtocolVersion a, ProtocolVersion b) => a.Equals(b);
    public static bool operator !=(ProtocolVersion a, ProtocolVersion b) => !a.Equals(b);
}
