using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using AwAUI.Platform;

namespace AwAUI.X11;

/// <summary>
/// 文件与目录选择器：优先用 zenity（没有时用 kdialog）经 <c>ProcessStartInfo.ArgumentList</c> 启动，
/// 不经过 shell；两个程序都不存在时返回空列表 / null（<see cref="StorageProvider"/> 允许的「不支持」结果）。
/// 未使用 xdg-desktop-portal：本后端没有现成的 D-Bus 助手，而 portal 需要额外的会话总线与接口定义。
/// <see cref="TryGetFileFromPathAsync"/> / <see cref="TryGetFolderFromPathAsync"/> 只用 System.IO，不依赖对话框。
/// </summary>
internal sealed class X11StorageProvider : StorageProvider
{
    readonly object _gate = new();
    bool _resolved;
    string? _executable;
    string? _tool;

    public override async Task<IReadOnlyList<StorageFile>> OpenFilesAsync(FilePickerOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        string? output = await RunAsync(X11FileDialogKind.Open, options).ConfigureAwait(false);
        var result = new List<StorageFile>();
        foreach (var item in X11FileDialogArguments.ParseOutput(output, options.AllowMultiple))
        {
            string? path = NormalizeExisting(item, directory: false);
            if (path is not null) result.Add(new X11StorageFile(path));
        }
        return result;
    }

    public override async Task<StorageFile?> SaveFileAsync(FilePickerOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        string? output = await RunAsync(X11FileDialogKind.Save, options).ConfigureAwait(false);
        var items = X11FileDialogArguments.ParseOutput(output, allowMultiple: false);
        if (items.Count == 0) return null;
        string? path = Normalize(items[0], mustExist: false);
        return path is null ? null : new X11StorageFile(path);
    }

    public override async Task<StorageFolder?> OpenFolderAsync(FilePickerOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        string? output = await RunAsync(X11FileDialogKind.Folder, options).ConfigureAwait(false);
        var items = X11FileDialogArguments.ParseOutput(output, allowMultiple: false);
        if (items.Count == 0) return null;
        string? path = NormalizeExisting(items[0], directory: true);
        return path is null ? null : new X11StorageFolder(path);
    }

    public override Task<StorageFile?> TryGetFileFromPathAsync(string path)
    {
        string? full = NormalizeExisting(path, directory: false);
        return Task.FromResult<StorageFile?>(full is null ? null : new X11StorageFile(full));
    }

    public override Task<StorageFolder?> TryGetFolderFromPathAsync(string path)
    {
        string? full = NormalizeExisting(path, directory: true);
        return Task.FromResult<StorageFolder?>(full is null ? null : new X11StorageFolder(full));
    }

    async Task<string?> RunAsync(X11FileDialogKind kind, FilePickerOptions options)
    {
        EnsureTool();
        string? executable = _executable;
        string? tool = _tool;
        if (executable is null || tool is null) return null;
        X11FileDialogCommand command = X11FileDialogArguments.Build(tool, kind, options);
        return await Task.Run(() => RunDialog(executable, command)).ConfigureAwait(false);
    }

    void EnsureTool()
    {
        if (_resolved) return;
        lock (_gate)
        {
            if (_resolved) return;
            _executable = FindExecutable("zenity");
            _tool = "zenity";
            if (_executable is null)
            {
                _executable = FindExecutable("kdialog");
                _tool = "kdialog";
            }
            _resolved = true;
        }
    }

    static string? FindExecutable(string name)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;
            try
            {
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            {
            }
        }
        return null;
    }

    static string? RunDialog(string executable, X11FileDialogCommand command)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
#if NETSTANDARD2_0
        var arguments = new System.Text.StringBuilder();
        foreach (var argument in command.Arguments)
        {
            if (arguments.Length > 0) arguments.Append(' ');
            arguments.Append(X11LaunchRules.QuotePosix(argument));
        }
        info.Arguments = arguments.ToString();
#else
        foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);
#endif
        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;
            // 同时排空 stdout / stderr（ReadCapped 超过上限后继续丢弃），避免任一管道写满导致子进程阻塞
            var stdout = Task.Run(() => X11FileDialogArguments.ReadCapped(process.StandardOutput, X11FileDialogArguments.MaxOutputLength));
            var stderr = Task.Run(() => X11FileDialogArguments.ReadCapped(process.StandardError, 4096));
            process.WaitForExit();
            string output = stdout.GetAwaiter().GetResult();
            stderr.GetAwaiter().GetResult();
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException or IOException)
        {
            return null;
        }
    }

    static string? NormalizeExisting(string? path, bool directory)
    {
        string? full = Normalize(path, mustExist: true);
        if (full is null) return null;
        try
        {
            return directory ? (Directory.Exists(full) ? full : null) : (File.Exists(full) ? full : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static string? Normalize(string? path, bool mustExist)
    {
        if (!X11LaunchRules.IsSafePath(path)) return null;
        try
        {
            string full = Path.GetFullPath(path!);
            if (full.Length > X11LaunchRules.MaxPathLength) return null;
            if (mustExist && !File.Exists(full) && !Directory.Exists(full)) return null;
            return full;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>基于 System.IO 的文件句柄。</summary>
internal sealed class X11StorageFile : StorageFile
{
    public X11StorageFile(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        if (Name.Length == 0) Name = path;
    }

    public override string Name { get; }
    public override string Path { get; }

    public override Task<Stream> OpenReadAsync() =>
        Task.FromResult<Stream>(new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));

    public override Task<Stream> OpenWriteAsync() =>
        Task.FromResult<Stream>(new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.None));

    public override Task<long> GetSizeAsync()
    {
        try { return Task.FromResult(new FileInfo(Path).Length); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Task.FromResult(-1L);
        }
    }
}

/// <summary>基于 System.IO 的目录句柄；枚举上限 10000 项。</summary>
internal sealed class X11StorageFolder : StorageFolder
{
    const int MaxEntries = 10000;

    public X11StorageFolder(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        if (Name.Length == 0) Name = path;
    }

    public override string Name { get; }
    public override string Path { get; }

    public override Task<IReadOnlyList<StorageFile>> GetFilesAsync()
    {
        var files = new List<StorageFile>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(Path))
            {
                files.Add(new X11StorageFile(path));
                if (files.Count >= MaxEntries) break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
        return Task.FromResult<IReadOnlyList<StorageFile>>(files);
    }

    public override Task<IReadOnlyList<StorageFolder>> GetFoldersAsync()
    {
        var folders = new List<StorageFolder>();
        try
        {
            foreach (var path in Directory.EnumerateDirectories(Path))
            {
                folders.Add(new X11StorageFolder(path));
                if (folders.Count >= MaxEntries) break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
        return Task.FromResult<IReadOnlyList<StorageFolder>>(folders);
    }
}
