using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AwAUI.Platform;

namespace AwAUI.Headless;

/// <summary>
/// 可脚本化的 <see cref="Launcher"/>（design/04「Launcher」）。记录每次启动请求并返回可配置的结果；
/// URI 只经基类 <see cref="Launcher.CanLaunchUri"/> 校验（默认 http / https / mailto），
/// 不接受的方案返回 false，但仍记录在 <see cref="LaunchUriCalls"/> 中，便于测试断言。
/// </summary>
public sealed class HeadlessLauncher : Launcher
{
    readonly List<Uri> _uriCalls = new();
    readonly List<StorageFile> _fileCalls = new();
    readonly List<StorageFolder> _folderCalls = new();

    /// <summary>受支持的请求返回的结果；默认 true。</summary>
    public bool Result { get; set; } = true;

    public IReadOnlyList<Uri> LaunchUriCalls => _uriCalls;

    public IReadOnlyList<StorageFile> LaunchFileCalls => _fileCalls;

    public IReadOnlyList<StorageFolder> LaunchFolderCalls => _folderCalls;

    /// <summary>全部启动调用（URI + 文件 + 目录）的次数。</summary>
    public int LaunchCount => _uriCalls.Count + _fileCalls.Count + _folderCalls.Count;

    public override Task<bool> LaunchUriAsync(Uri uri)
    {
        if (uri is null) return Task.FromResult(false);
        _uriCalls.Add(uri);
        if (!CanLaunchUri(uri)) return Task.FromResult(false);
        return Task.FromResult(Result);
    }

    public override Task<bool> LaunchFileAsync(StorageFile file)
    {
        if (file is null) return Task.FromResult(false);
        _fileCalls.Add(file);
        return Task.FromResult(Result);
    }

    public override Task<bool> LaunchFolderAsync(StorageFolder folder)
    {
        if (folder is null) return Task.FromResult(false);
        _folderCalls.Add(folder);
        return Task.FromResult(Result);
    }

    /// <summary>清空记录的调用。</summary>
    public void ClearCalls()
    {
        _uriCalls.Clear();
        _fileCalls.Clear();
        _folderCalls.Clear();
    }
}
