using System;
using System.IO;
using System.Threading.Tasks;
using AwAUI.Platform;
using AwAUI.Win32.Interop;
using static AwAUI.Win32.Interop.Const;

namespace AwAUI.Win32;

/// <summary>
/// 用系统默认处理程序打开 URI / 文件 / 目录（design/04「Launcher」）。只把严格校验过的绝对 URI
/// （http / https / mailto）与绝对文件系统路径交给 ShellExecuteW；返回值 &gt; 32 视为成功。
/// 调用在 UI 线程执行；任何平台失败都返回 false，不抛异常。
/// </summary>
public sealed class Win32Launcher : Launcher
{
    readonly Win32Platform _platform;

    internal Win32Launcher(Win32Platform platform) => _platform = platform;

    public override bool CanLaunchUri(Uri? uri) => Win32Path.IsSafeLaunchUri(uri);

    public override Task<bool> LaunchUriAsync(Uri uri)
    {
        if (!Win32Path.IsSafeLaunchUri(uri)) return Task.FromResult(false);
        string target = uri.AbsoluteUri;
        return OnUiThread(() => ShellExecute(target));
    }

    public override Task<bool> LaunchFileAsync(StorageFile file)
    {
        if (file is null || !Win32Path.IsSafeAbsolutePath(file.Path) || !File.Exists(file.Path))
            return Task.FromResult(false);
        string path = file.Path;
        return OnUiThread(() => ShellExecute(path));
    }

    public override Task<bool> LaunchFolderAsync(StorageFolder folder)
    {
        if (folder is null || !Win32Path.IsSafeAbsolutePath(folder.Path) || !Directory.Exists(folder.Path))
            return Task.FromResult(false);
        string path = folder.Path;
        return OnUiThread(() => ShellExecute(path));
    }

    bool ShellExecute(string target)
    {
        var owner = User32.GetActiveWindow();
        if (owner == IntPtr.Zero) owner = _platform.MessageWindow;
        return Execute(owner, target);
    }

    static unsafe bool Execute(IntPtr owner, string target)
    {
        if (target.IndexOf('\0') >= 0) return false;
        try
        {
            fixed (char* t = target)
            fixed (char* open = "open")
            {
                // 参数与工作目录留空：目标本身已通过绝对 URI / 绝对路径校验。
                var result = Shell32.ShellExecuteW(owner, open, t, null, null, SW_SHOWNORMAL);
                return (long)result > 32;
            }
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    Task<T> OnUiThread<T>(Func<T> action)
    {
        if (_platform.Dispatcher.CheckAccess())
        {
            try { return Task.FromResult(action()); }
            catch (Exception e) when (e is not OutOfMemoryException) { return Task.FromException<T>(e); }
        }
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _platform.Dispatcher.Post(() =>
        {
            try { tcs.TrySetResult(action()); }
            catch (Exception e) { tcs.TrySetException(e); }
        });
        return tcs.Task;
    }
}
