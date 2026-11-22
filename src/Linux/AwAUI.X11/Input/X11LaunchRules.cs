using System;
using System.Collections.Generic;
using System.IO;

namespace AwAUI.X11;

/// <summary>
/// 用系统默认程序打开 URI / 文件前的严格校验（纯逻辑）。<see cref="X11Launcher"/> 只把通过校验的参数交给
/// xdg-open：URI 必须是绝对 URI 且属于允许的方案，文件路径必须是本机绝对路径，且不含 NUL / 控制字符。
/// </summary>
internal static class X11LaunchRules
{
    /// <summary>URI 长度上限，避免把异常大的字符串交给外部程序。</summary>
    public const int MaxUriLength = 8192;

    /// <summary>Linux 的 PATH_MAX；更长的路径直接拒绝。</summary>
    public const int MaxPathLength = 4096;

    static readonly HashSet<string> AllowedSchemes = new(StringComparer.Ordinal)
    {
        Uri.UriSchemeHttp, Uri.UriSchemeHttps, "mailto", "tel", "ftp", Uri.UriSchemeFile,
    };

    /// <summary>是否允许用系统默认程序打开该 URI。</summary>
    public static bool CanLaunch(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri) return false;
        if (!AllowedSchemes.Contains(uri.Scheme)) return false;
        if (uri.OriginalString.Length == 0 || uri.OriginalString.Length > MaxUriLength) return false;
        if (HasControlCharacters(uri.OriginalString)) return false;
        // 不把凭据写进交给外部程序的命令行（mailto 的「user」会被 Uri 解析成 UserInfo，不属于凭据）
        if (uri.UserInfo.Length > 0 && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == "ftp"))
            return false;
        if (uri.IsFile)
        {
            // 只打开本机文件，不接受 file://otherhost/...
            if (!string.IsNullOrEmpty(uri.Host) && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                return false;
            return IsSafePath(uri.LocalPath);
        }
        return true;
    }

    /// <summary>是否是可供外部程序打开的本机绝对路径。</summary>
    public static bool IsSafePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string value = path!;
        if (value.Length > MaxPathLength) return false;
        if (HasControlCharacters(value)) return false;
        try
        {
            if (!Path.IsPathRooted(value)) return false;
        }
        catch (ArgumentException) { return false; }
        // 拒绝 ".." 段：外部程序可能按自己的规则规范化路径，不留下歧义
        string[] segments = value.Split('/');
        foreach (var segment in segments)
            if (segment == "..") return false;
        return true;
    }

    /// <summary>
    /// 为 netstandard2.0 的 <c>ProcessStartInfo.Arguments</c> 生成单个 POSIX 参数：用单引号包裹并把内部的
    /// 单引号写成 <c>'\''</c>。新目标框架使用 <c>ArgumentList</c>，不经过字符串拼接。
    /// </summary>
    public static string QuotePosix(string argument)
    {
        if (argument is null) throw new ArgumentNullException(nameof(argument));
        if (argument.Length == 0) return "''";
        var sb = new System.Text.StringBuilder(argument.Length + 8);
        sb.Append('\'');
        foreach (var c in argument)
        {
            if (c == '\'') sb.Append("'\\''");
            else sb.Append(c);
        }
        sb.Append('\'');
        return sb.ToString();
    }

    /// <summary>是否含有 NUL 或其他 C0 / DEL 控制字符。</summary>
    public static bool HasControlCharacters(string text)
    {
        foreach (var c in text)
            if (c < 0x20 || c == 0x7F) return true;
        return false;
    }
}
