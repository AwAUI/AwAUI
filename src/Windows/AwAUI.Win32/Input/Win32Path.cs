using System;
using System.IO;

namespace AwAUI.Win32;

// 纯逻辑：启动器与文件选择器的输入校验（不引用互操作，可跨平台测试）。

/// <summary>
/// 启动器与 StorageProvider 的严格输入校验。ShellExecuteW 只接受明确的绝对路径/受支持的绝对 URI；
/// 任何控制字符、超长字符串与相对路径都被拒绝，避免把任意字符串拼进 shell 命令。
/// </summary>
internal static class Win32Path
{
    /// <summary>Windows 路径长度上限（UTF-16 码元）。</summary>
    public const int MaxPath = 32767;

    /// <summary>可接受的绝对 URI 长度上限（防止病态输入）。</summary>
    public const int MaxUri = 8192;

    /// <summary>是否是可用于启动/打开的绝对文件系统路径（不要求文件存在）。</summary>
    public static bool IsSafeAbsolutePath(string? path)
    {
        if (path is null || path.Length == 0 || path.Length > MaxPath) return false;
        for (int i = 0; i < path.Length; i++)
            if (path[i] == '\0' || char.IsControl(path[i])) return false;
        if (!Path.IsPathRooted(path)) return false;
        try
        {
            return Path.GetFullPath(path).Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 是否是启动器愿意交给 ShellExecuteW 的 URI：绝对、长度受限、无控制字符，方案仅限
    /// http / https / mailto（与 <see cref="AwAUI.Platform.Launcher.CanLaunchUri"/> 的默认策略一致）。
    /// </summary>
    public static bool IsSafeLaunchUri(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri) return false;
        string text = uri.AbsoluteUri;
        if (text.Length == 0 || text.Length > MaxUri) return false;
        for (int i = 0; i < text.Length; i++)
            if (char.IsControl(text[i])) return false;
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == "mailto";
    }

    /// <summary>目录 + 名称 组合为绝对路径；越界或非法时返回 null。</summary>
    public static string? Combine(string? directory, string? name)
    {
        if (directory is null || directory.Length == 0 || name is null || name.Length == 0) return null;
        if (name.Length > MaxPath || directory.Length > MaxPath) return null;
        if (name.IndexOf('\0') >= 0 || directory.IndexOf('\0') >= 0) return null;
        string combined;
        try
        {
            combined = Path.Combine(directory, name);
        }
        catch (Exception)
        {
            return null;
        }
        return combined.Length <= MaxPath && IsSafeAbsolutePath(combined) ? combined : null;
    }
}
