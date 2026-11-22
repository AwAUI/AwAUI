using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace AwAUI.Platform;

/// <summary>文件选择器的文件类型过滤项（design/04「文件对话框」）。</summary>
public sealed class FilePickerFileType
{
    public FilePickerFileType(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    /// <summary>显示名称，例如 "PNG 图片"。</summary>
    public string Name { get; }

    /// <summary>通配符模式，例如 <c>*.png</c>；由平台按自身规则转换。</summary>
    public IList<string> Patterns { get; } = new List<string>();

    /// <summary>MIME 类型（Web 与 Linux portal 使用）。</summary>
    public IList<string> MimeTypes { get; } = new List<string>();

    /// <summary>Apple Uniform Type Identifier（macOS 使用）。</summary>
    public IList<string> AppleUniformTypeIdentifiers { get; } = new List<string>();
}

/// <summary>打开/保存/选择目录的选项。</summary>
public sealed class FilePickerOptions
{
    /// <summary>对话框标题。</summary>
    public string? Title { get; set; }

    /// <summary>建议的文件名（保存对话框）。</summary>
    public string? SuggestedFileName { get; set; }

    /// <summary>默认扩展名（保存对话框，不含点）。</summary>
    public string? DefaultExtension { get; set; }

    /// <summary>类型过滤；为空表示平台默认。</summary>
    public IList<FilePickerFileType> FileTypes { get; } = new List<FilePickerFileType>();

    /// <summary>是否允许多选（打开文件）。</summary>
    public bool AllowMultiple { get; set; }
}

/// <summary>平台文件句柄。实现方负责把路径与读写流映射到真实文件或浏览器文件对象。</summary>
public abstract class StorageFile
{
    /// <summary>不含目录的文件名。</summary>
    public abstract string Name { get; }

    /// <summary>完整路径或平台可识别的定位字符串；Web 上可能为空。</summary>
    public abstract string Path { get; }

    /// <summary>打开只读流；调用方负责释放。</summary>
    public abstract Task<Stream> OpenReadAsync();

    /// <summary>打开写入流（截断已有内容）；调用方负责释放。</summary>
    public abstract Task<Stream> OpenWriteAsync();

    /// <summary>文件大小（字节）；未知时返回 -1。</summary>
    public virtual Task<long> GetSizeAsync() => Task.FromResult(-1L);

    public override string ToString() => Path.Length > 0 ? Path : Name;
}

/// <summary>平台目录句柄。</summary>
public abstract class StorageFolder
{
    public abstract string Name { get; }
    public abstract string Path { get; }

    /// <summary>枚举直接子文件；不支持时返回空列表。</summary>
    public virtual Task<IReadOnlyList<StorageFile>> GetFilesAsync() =>
        Task.FromResult<IReadOnlyList<StorageFile>>(Array.Empty<StorageFile>());

    /// <summary>枚举直接子目录；不支持时返回空列表。</summary>
    public virtual Task<IReadOnlyList<StorageFolder>> GetFoldersAsync() =>
        Task.FromResult<IReadOnlyList<StorageFolder>>(Array.Empty<StorageFolder>());

    public override string ToString() => Path.Length > 0 ? Path : Name;
}

/// <summary>
/// 平台文件与目录选择器（design/04）。所有调用都在 UI 线程发起，返回的任务可在其他线程完成。
/// 平台不支持某项能力时返回 null 或空列表，而不是抛异常。
/// </summary>
public abstract class StorageProvider
{
    /// <summary>打开一个或多个文件。用户取消时返回空列表。</summary>
    public abstract Task<IReadOnlyList<StorageFile>> OpenFilesAsync(FilePickerOptions options);

    /// <summary>打开单个文件（<see cref="FilePickerOptions.AllowMultiple"/> 为 false 时的便捷方法）。</summary>
    public virtual async Task<StorageFile?> OpenFileAsync(FilePickerOptions options)
    {
        var files = await OpenFilesAsync(options).ConfigureAwait(false);
        return files.Count > 0 ? files[0] : null;
    }

    /// <summary>选择保存位置；用户取消时返回 null。</summary>
    public abstract Task<StorageFile?> SaveFileAsync(FilePickerOptions options);

    /// <summary>选择目录；用户取消时返回 null。</summary>
    public abstract Task<StorageFolder?> OpenFolderAsync(FilePickerOptions options);

    /// <summary>从路径取得已有文件；路径无效或平台不支持时返回 null。</summary>
    public virtual Task<StorageFile?> TryGetFileFromPathAsync(string path) => Task.FromResult<StorageFile?>(null);

    /// <summary>从路径取得已有目录；路径无效或平台不支持时返回 null。</summary>
    public virtual Task<StorageFolder?> TryGetFolderFromPathAsync(string path) => Task.FromResult<StorageFolder?>(null);
}
