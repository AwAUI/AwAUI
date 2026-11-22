using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using AwAUI.Platform;

namespace AwAUI.X11;

/// <summary>
/// 用系统默认程序打开 URI / 文件：通过 <c>xdg-open</c>（不经过 shell，参数逐个传递）。URI 与路径先经过
/// <see cref="X11LaunchRules"/> 的严格校验；xdg-open 不存在或启动失败时返回 false，不抛异常。
/// xdg-open 之后由桌面环境接管，调用返回时子进程可能仍在运行。
/// </summary>
internal sealed class X11Launcher : Launcher
{
    public override bool CanLaunchUri(Uri? uri) => X11LaunchRules.CanLaunch(uri);

    public override Task<bool> LaunchUriAsync(Uri uri)
    {
        if (!X11LaunchRules.CanLaunch(uri)) return Task.FromResult(false);
        return Task.FromResult(Start(uri.AbsoluteUri));
    }

    public override Task<bool> LaunchFileAsync(StorageFile file)
    {
        string? path = file?.Path;
        if (!X11LaunchRules.IsSafePath(path)) return Task.FromResult(false);
        try
        {
            if (!File.Exists(path)) return Task.FromResult(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(false);
        }
        return Task.FromResult(Start(path!));
    }

    public override Task<bool> LaunchFolderAsync(StorageFolder folder)
    {
        string? path = folder?.Path;
        if (!X11LaunchRules.IsSafePath(path)) return Task.FromResult(false);
        try
        {
            if (!Directory.Exists(path)) return Task.FromResult(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(false);
        }
        return Task.FromResult(Start(path!));
    }

    static bool Start(string argument)
    {
        var info = new ProcessStartInfo
        {
            FileName = "xdg-open",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
#if NETSTANDARD2_0
        // netstandard2.0 没有 ArgumentList：用严格引用的单个 POSIX 参数（仍然不经过 shell）
        info.Arguments = X11LaunchRules.QuotePosix(argument);
#else
        info.ArgumentList.Add(argument);
#endif
        try
        {
            using var process = Process.Start(info);
            return process is not null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
