using System;
using System.Threading.Tasks;
using AwAUI.Browser.Interop;
using AwAUI.Platform;

namespace AwAUI.Browser;

/// <summary>
/// Web 上的 <see cref="Launcher"/>：浏览器没有 shell，只能在新的浏览上下文里打开 URI。
/// 只接受 <see cref="BrowserUriPolicy"/> 允许的方案，并交给 JS 的 <c>window.open(..., "noopener,noreferrer")</c>；
/// 用户手势缺失、弹出窗口被拦截或宿主环境没有 <c>window.open</c> 时返回 false。
/// <para>
/// 文件与目录无法用默认程序打开（没有本机路径），<see cref="LaunchFileAsync"/> / <see cref="LaunchFolderAsync"/> 保持基类的 false。
/// </para>
/// </summary>
internal sealed class BrowserLauncher : Launcher
{
    public override bool CanLaunchUri(Uri? uri) => BrowserUriPolicy.IsAllowed(uri);

    public override Task<bool> LaunchUriAsync(Uri uri)
    {
        if (!CanLaunchUri(uri)) return Task.FromResult(false);
        try
        {
            // 同步调用：必须在用户手势的同一个任务里进入 window.open，await 会丢失 transient activation。
            return Task.FromResult(BrowserInterop.LauncherOpen(uri.AbsoluteUri));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Task.FromResult(false);
        }
    }
}
