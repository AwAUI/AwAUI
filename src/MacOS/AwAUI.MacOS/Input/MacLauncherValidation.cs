using System;
using System.IO;

namespace AwAUI.MacOS;

/// <summary>
/// Launcher 的 URI / 路径校验（与 AppKit 无关，放在 Input/ 目录里以便单元测试）。
/// 只允许明确的绝对 URI 方案与绝对文件路径；拒绝相对路径、控制字符与把任意字符串拼进系统调用的可能。
/// </summary>
internal static class MacLauncherValidation
{
    /// <summary>系统默认程序可以打开的 URI 方案白名单。</summary>
    public static bool CanLaunch(Uri? uri)
    {
        if (uri is not { IsAbsoluteUri: true }) return false;
        return uri.Scheme switch
        {
            "http" or "https" or "mailto" => true,
            // 文件 URI：必须是本机文件路径，且是绝对的、不含控制字符
            "file" => uri.IsFile && IsSafePath(uri.LocalPath),
            _ => false,
        };
    }

    /// <summary>路径字符串是否可以安全地交给 AppKit：非空、绝对、无 NUL 或其他控制字符。</summary>
    public static bool IsSafePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var value = path!;
        foreach (char c in value)
            if (c < 0x20 || c == 0x7F) return false;
        try { return Path.IsPathRooted(value); }
        catch (ArgumentException) { return false; }
    }
}
