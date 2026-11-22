using System;
using System.Threading.Tasks;

namespace AwAUI.Platform;

/// <summary>
/// 用系统默认程序打开 URI、文件或目录（design/04「Launcher」）。
/// 实现必须校验 URI：只允许明确的绝对 URI/受支持方案，拒绝把任意字符串拼进命令行。
/// </summary>
public abstract class Launcher
{
    /// <summary>用默认处理程序打开 URI；成功启动返回 true。</summary>
    public abstract Task<bool> LaunchUriAsync(Uri uri);

    /// <summary>用默认程序打开文件；成功启动返回 true。默认不支持。</summary>
    public virtual Task<bool> LaunchFileAsync(StorageFile file) => Task.FromResult(false);

    /// <summary>用文件管理器显示目录；成功启动返回 true。默认不支持。</summary>
    public virtual Task<bool> LaunchFolderAsync(StorageFolder folder) => Task.FromResult(false);

    /// <summary>平台是否能处理该 URI。默认只接受 http/https/mailto。</summary>
    public virtual bool CanLaunchUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == "mailto");
}
