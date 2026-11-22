using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AwAUI.Browser.Interop;
using AwAUI.Platform;

namespace AwAUI.Browser;

/// <summary>
/// Web 上的文件与目录选择器。浏览器没有路径：文件由 JS 端隐藏的 <c>&lt;input type=file&gt;</c> 选择，
/// 保存优先使用 File System Access 的保存对话框，不支持时退化为浏览器下载。
/// <para>
/// 打开的文件内容在 <see cref="StorageFile.OpenReadAsync"/> 时整体读入内存（上限 <see cref="MaxFileBytes"/>）；
/// 写入缓冲在流释放时提交。目录选择（<see cref="OpenFolderAsync"/>）在浏览器中没有可移植的实现，返回 null。
/// </para>
/// </summary>
internal sealed class BrowserStorageProvider : StorageProvider
{
    /// <summary>单个文件读入 / 写入缓冲的上限（256 MB）。超过时抛出 <see cref="IOException"/> 而不是耗尽 WASM 堆。</summary>
    public const long MaxFileBytes = 256L * 1024 * 1024;

    public override async Task<IReadOnlyList<StorageFile>> OpenFilesAsync(FilePickerOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        try
        {
            string? payload = await BrowserInterop
                .StorageOpenFiles(BrowserStorageCodec.BuildAccept(options), options.AllowMultiple)
                .ConfigureAwait(true);
            return Parse(payload);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Array.Empty<StorageFile>();
        }
    }

    public override async Task<StorageFile?> SaveFileAsync(FilePickerOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        string name = options.SuggestedFileName ?? "download";
        if (!string.IsNullOrEmpty(options.DefaultExtension)
            && !name.EndsWith("." + options.DefaultExtension, StringComparison.OrdinalIgnoreCase))
        {
            name += "." + options.DefaultExtension;
        }

        try
        {
            string? payload = await BrowserInterop.StorageSaveFile(name).ConfigureAwait(true);
            var files = Parse(payload);
            return files.Count > 0 ? files[0] : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>浏览器没有可移植的目录选择器；返回 null（而不是假装有一个空目录）。</summary>
    public override Task<StorageFolder?> OpenFolderAsync(FilePickerOptions options) => Task.FromResult<StorageFolder?>(null);

    static IReadOnlyList<StorageFile> Parse(string? payload)
    {
        var entries = BrowserStorageCodec.Parse(payload);
        var files = new List<StorageFile>(entries.Count);
        foreach (var entry in entries) files.Add(new BrowserStorageFile(entry));
        return files;
    }
}

/// <summary>
/// 浏览器文件句柄：包装 JS 端注册表里的 <c>File</c>（只读）或 <c>FileSystemFileHandle</c>（可读写）。
/// <see cref="Path"/> 为空，因为浏览器不暴露路径；读取通过 JS 把文件内容写入 WASM 线性内存中的固定缓冲区完成。
/// </summary>
internal sealed class BrowserStorageFile : StorageFile
{
    readonly int _handle;
    readonly string _name;
    readonly bool _writable;
    long _size;

    internal BrowserStorageFile(in BrowserStorageEntry entry)
    {
        _handle = entry.Handle;
        _name = entry.Name;
        _writable = entry.Writable;
        _size = entry.Size;
    }

    public override string Name => _name;

    /// <summary>浏览器没有文件系统路径；返回空串（<see cref="StorageFile.Path"/> 允许为空）。</summary>
    public override string Path => string.Empty;

    public override async Task<long> GetSizeAsync()
    {
        if (_size >= 0) return _size;
        try
        {
            double size = await BrowserInterop.StorageFileSize(_handle).ConfigureAwait(true);
            if (size >= 0 && size <= long.MaxValue) _size = (long)size;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 保存对话框选中的新文件在写入前可能还不存在：保持 -1（未知）。
        }
        return _size;
    }

    public override async Task<Stream> OpenReadAsync()
    {
        long size = await GetSizeAsync().ConfigureAwait(true);
        if (size < 0) throw new IOException("The browser could not report the size of the file.");
        if (size > BrowserStorageProvider.MaxFileBytes)
            throw new IOException($"The browser file is too large to read into memory ({size} bytes).");
        var bytes = new byte[checked((int)size)];
        if (bytes.Length > 0)
        {
            var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                if (!await BrowserInterop.StorageReadFileInto(_handle, Address(pinned), bytes.Length).ConfigureAwait(true))
                    throw new IOException("The browser could not read the file.");
            }
            finally
            {
                pinned.Free();
            }
        }
        return new MemoryStream(bytes, writable: false);
    }

    public override Task<Stream> OpenWriteAsync()
    {
        if (!_writable)
            throw new NotSupportedException("A file opened from an <input type=file> is read-only in the browser.");
        return Task.FromResult<Stream>(new BrowserWriteStream(_handle));
    }

    internal static int Address(GCHandle pinned) => checked((int)pinned.AddrOfPinnedObject().ToInt64());
}

/// <summary>
/// 浏览器保存流：先写入内存，<see cref="DisposeAsync"/>（或 <see cref="FlushAsync"/>）时把缓冲区交给 JS，
/// 由 File System Access 写入磁盘或退化为下载。同步的 <see cref="Dispose()"/> 会尽力启动提交，
/// 但只有 <c>await using</c> 能观察到失败。
/// </summary>
internal sealed class BrowserWriteStream : Stream
{
    readonly int _handle;
    readonly MemoryStream _buffer = new();
    long _position;
    bool _disposed;
    bool _committed;

    internal BrowserWriteStream(int handle) => _handle = handle;

    public override bool CanRead => false;
    public override bool CanSeek => true;
    public override bool CanWrite => !_disposed;
    public override long Length => _buffer.Length;

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _position = value;
        }
    }

    public override void Flush() { }

    /// <summary>把目前写入的内容提交到浏览器；之后的写入仍会留在缓冲区，但不会再次提交（避免下载被触发两次）。</summary>
    public override Task FlushAsync(CancellationToken cancellationToken) => CommitAsync();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _buffer.Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0) throw new IOException("Cannot seek before the beginning of the stream.");
        _position = target;
        return target;
    }

    public override void SetLength(long value) => _buffer.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BrowserWriteStream));
        _buffer.Position = _position;
        _buffer.Write(buffer, offset, count);
        _position = _buffer.Position;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            // 没有等待者：失败无法报告（调用方应使用 DisposeAsync / await using）。
            _ = CommitAndReleaseAsync();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            try
            {
                await CommitAsync().ConfigureAwait(true);
            }
            finally
            {
                _buffer.Dispose();
                await base.DisposeAsync().ConfigureAwait(true);
            }
            return;
        }
        await base.DisposeAsync().ConfigureAwait(true);
    }

    async Task CommitAndReleaseAsync()
    {
        try
        {
            await CommitAsync().ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 同步 Dispose 的路径没有调用方可以抛给（只作为尽力而为的提交）。
        }
        finally
        {
            _buffer.Dispose();
        }
    }

    async Task CommitAsync()
    {
        if (_committed) return;
        _committed = true;
        var bytes = _buffer.GetBuffer();
        int length = checked((int)_buffer.Length);
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            bool ok = await BrowserInterop.StorageFinishWrite(_handle, BrowserStorageFile.Address(pinned), length).ConfigureAwait(true);
            if (!ok) throw new IOException("The browser rejected the save request.");
        }
        finally
        {
            pinned.Free();
        }
    }
}
