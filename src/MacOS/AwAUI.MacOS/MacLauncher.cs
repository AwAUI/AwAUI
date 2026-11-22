using System;
using System.Threading.Tasks;
using AwAUI.Platform;
using static AwAUI.MacOS.Interop.ObjC;

namespace AwAUI.MacOS;

/// <summary>
/// 用系统默认程序打开 URI / 文件 / 目录：<c>[NSWorkspace openURL:]</c> 与 <c>openFile:</c>。
/// 只接受白名单方案与绝对路径（见 <see cref="MacLauncherValidation"/>），拒绝把任意字符串交给系统。
/// </summary>
internal sealed class MacLauncher : Launcher
{
    readonly MacPlatform _platform;

    internal MacLauncher(MacPlatform platform) => _platform = platform;

    public override bool CanLaunchUri(Uri? uri) => MacLauncherValidation.CanLaunch(uri);

    public override Task<bool> LaunchUriAsync(Uri uri)
    {
        if (uri is null || !CanLaunchUri(uri)) return Task.FromResult(false);
        return MacMainThread.Invoke(_platform, () => OpenUri(uri.AbsoluteUri));
    }

    public override Task<bool> LaunchFileAsync(StorageFile file)
    {
        if (file is null || !MacLauncherValidation.IsSafePath(file.Path) || !MacStoragePaths.Exists(file.Path, directory: false))
            return Task.FromResult(false);
        return MacMainThread.Invoke(_platform, () => OpenPath(file.Path));
    }

    public override Task<bool> LaunchFolderAsync(StorageFolder folder)
    {
        if (folder is null || !MacLauncherValidation.IsSafePath(folder.Path) || !MacStoragePaths.Exists(folder.Path, directory: true))
            return Task.FromResult(false);
        return MacMainThread.Invoke(_platform, () => OpenPath(folder.Path));
    }

    static bool OpenUri(string absoluteUri)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var ns = NewString(absoluteUri);
            var url = Send(Class("NSURL"), Sel("URLWithString:"), ns);
            Release(ns);
            if (url == IntPtr.Zero) return false;
            var workspace = Send(Class("NSWorkspace"), Sel("sharedWorkspace"));
            return workspace != IntPtr.Zero && SendByte(workspace, Sel("openURL:"), url) != 0;
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    static bool OpenPath(string path)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var ns = NewString(path);
            var workspace = Send(Class("NSWorkspace"), Sel("sharedWorkspace"));
            bool ok = workspace != IntPtr.Zero && SendByte(workspace, Sel("openFile:"), ns) != 0;
            Release(ns);
            return ok;
        }
        finally { objc_autoreleasePoolPop(pool); }
    }
}
