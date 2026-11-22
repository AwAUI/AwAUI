using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AwAUI.Platform;
using IOPath = System.IO.Path;

namespace AwAUI.Headless;

/// <summary>
/// 可脚本化的文件与目录选择器（design/04「文件对话框」）。调用按队列返回预设结果（空队列表示用户取消），
/// 并记录每次请求的选项。文件与目录既可以是纯内存对象（<see cref="HeadlessStorageFile"/> /
/// <see cref="HeadlessStorageFolder"/>），也可以位于 <see cref="TempRoot"/> 下的真实临时路径。
/// 不弹任何窗口，结果完全确定。
/// </summary>
public sealed class HeadlessStorageProvider : StorageProvider
{
    readonly Queue<IReadOnlyList<StorageFile>> _openQueue = new();
    readonly Queue<StorageFile?> _saveQueue = new();
    readonly Queue<StorageFolder?> _folderQueue = new();
    readonly List<FilePickerOptions> _openRequests = new();
    readonly List<FilePickerOptions> _saveRequests = new();
    readonly List<FilePickerOptions> _folderRequests = new();

    /// <param name="tempRoot">临时路径的根目录；为 null 时使用系统临时目录下的 <c>AwAUI.Headless</c>。</param>
    public HeadlessStorageProvider(string? tempRoot = null)
    {
        TempRoot = string.IsNullOrEmpty(tempRoot)
            ? IOPath.Combine(IOPath.GetTempPath(), "AwAUI.Headless")
            : tempRoot!;
    }

    /// <summary><see cref="CreateTempFile"/> 与 <see cref="CreateTempFolder"/> 使用的根目录。</summary>
    public string TempRoot { get; }

    /// <summary>每一次 <see cref="OpenFilesAsync"/> 的选项（按调用顺序）。</summary>
    public IReadOnlyList<FilePickerOptions> OpenFilesRequests => _openRequests;

    /// <summary>每一次 <see cref="SaveFileAsync"/> 的选项（按调用顺序）。</summary>
    public IReadOnlyList<FilePickerOptions> SaveFileRequests => _saveRequests;

    /// <summary>每一次 <see cref="OpenFolderAsync"/> 的选项（按调用顺序）。</summary>
    public IReadOnlyList<FilePickerOptions> OpenFolderRequests => _folderRequests;

    public int PendingOpenFiles => _openQueue.Count;
    public int PendingSaveFiles => _saveQueue.Count;
    public int PendingFolders => _folderQueue.Count;

    // ---------------------------------------------------------------- 队列

    /// <summary>排入一次 "打开文件" 的结果；可一次给出多个文件。排入空列表等价于用户取消。</summary>
    public void EnqueueOpenFiles(params StorageFile[] files) =>
        EnqueueOpenFiles((IEnumerable<StorageFile>)(files ?? throw new ArgumentNullException(nameof(files))));

    /// <summary>排入一次 "打开文件" 的结果；可一次给出多个文件。</summary>
    public void EnqueueOpenFiles(IEnumerable<StorageFile> files)
    {
        if (files is null) throw new ArgumentNullException(nameof(files));
        var copy = new List<StorageFile>();
        foreach (var file in files)
            copy.Add(file ?? throw new ArgumentException("Files cannot contain null.", nameof(files)));
        _openQueue.Enqueue(copy);
    }

    /// <summary>排入一次 "打开单个文件" 的结果；null 表示取消。</summary>
    public void EnqueueOpenFile(StorageFile? file) =>
        EnqueueOpenFiles(file is null ? Array.Empty<StorageFile>() : new[] { file });

    /// <summary>排入一次保存对话框的结果；null 表示取消。</summary>
    public void EnqueueSaveFile(StorageFile? file) => _saveQueue.Enqueue(file);

    /// <summary>排入一次目录选择的结果；null 表示取消。</summary>
    public void EnqueueOpenFolder(StorageFolder? folder) => _folderQueue.Enqueue(folder);

    /// <summary>丢弃全部排队结果（不影响已记录的请求）。</summary>
    public void ClearQueue()
    {
        _openQueue.Clear();
        _saveQueue.Clear();
        _folderQueue.Clear();
    }

    /// <summary>清空记录的请求（不影响排队结果）。</summary>
    public void ClearRequests()
    {
        _openRequests.Clear();
        _saveRequests.Clear();
        _folderRequests.Clear();
    }

    // ---------------------------------------------------------------- StorageProvider

    public override Task<IReadOnlyList<StorageFile>> OpenFilesAsync(FilePickerOptions options)
    {
        _openRequests.Add(options ?? throw new ArgumentNullException(nameof(options)));
        IReadOnlyList<StorageFile> result = _openQueue.Count > 0
            ? _openQueue.Dequeue()
            : Array.Empty<StorageFile>();
        return Task.FromResult(result);
    }

    public override Task<StorageFile?> SaveFileAsync(FilePickerOptions options)
    {
        _saveRequests.Add(options ?? throw new ArgumentNullException(nameof(options)));
        return Task.FromResult(_saveQueue.Count > 0 ? _saveQueue.Dequeue() : null);
    }

    public override Task<StorageFolder?> OpenFolderAsync(FilePickerOptions options)
    {
        _folderRequests.Add(options ?? throw new ArgumentNullException(nameof(options)));
        return Task.FromResult(_folderQueue.Count > 0 ? _folderQueue.Dequeue() : null);
    }

    public override Task<StorageFile?> TryGetFileFromPathAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return Task.FromResult<StorageFile?>(null);
        try
        {
            if (!File.Exists(path)) return Task.FromResult<StorageFile?>(null);
            return Task.FromResult<StorageFile?>(HeadlessStorageFile.FromTempPath(IOPath.GetFullPath(path)));
        }
        catch (ArgumentException)
        {
            return Task.FromResult<StorageFile?>(null);
        }
        catch (NotSupportedException)
        {
            return Task.FromResult<StorageFile?>(null);
        }
    }

    public override Task<StorageFolder?> TryGetFolderFromPathAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return Task.FromResult<StorageFolder?>(null);
        try
        {
            if (!Directory.Exists(path)) return Task.FromResult<StorageFolder?>(null);
            return Task.FromResult<StorageFolder?>(HeadlessStorageFolder.FromTempPath(IOPath.GetFullPath(path)));
        }
        catch (ArgumentException)
        {
            return Task.FromResult<StorageFolder?>(null);
        }
        catch (NotSupportedException)
        {
            return Task.FromResult<StorageFolder?>(null);
        }
    }

    // ---------------------------------------------------------------- 内存对象

    /// <summary>创建一个纯内存文件；<paramref name="content"/> 被复制。</summary>
    public HeadlessStorageFile CreateFile(string name, byte[]? content = null) => new(name, content);

    /// <summary>创建一个纯内存文件，内容为 UTF-8 编码的 <paramref name="text"/>。</summary>
    public HeadlessStorageFile CreateFile(string name, string text) =>
        new(name, Encoding.UTF8.GetBytes(text ?? throw new ArgumentNullException(nameof(text))));

    /// <summary>创建一个纯内存目录。</summary>
    public HeadlessStorageFolder CreateFolder(string name) => new(name);

    // ---------------------------------------------------------------- 临时路径

    /// <summary>在 <see cref="TempRoot"/> 下创建（或覆盖）一个文件并返回它的句柄。</summary>
    public HeadlessStorageFile CreateTempFile(string name, byte[]? content = null)
    {
        string path = ResolveTempPath(name);
        string? directory = IOPath.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory!);
        File.WriteAllBytes(path, content ?? Array.Empty<byte>());
        return HeadlessStorageFile.FromTempPath(path);
    }

    /// <summary>在 <see cref="TempRoot"/> 下创建（或覆盖）一个 UTF-8 文本文件并返回它的句柄。</summary>
    public HeadlessStorageFile CreateTempFile(string name, string text) =>
        CreateTempFile(name, Encoding.UTF8.GetBytes(text ?? throw new ArgumentNullException(nameof(text))));

    /// <summary>在 <see cref="TempRoot"/> 下创建（或复用）一个目录并返回它的句柄。</summary>
    public HeadlessStorageFolder CreateTempFolder(string name)
    {
        string path = ResolveTempPath(name);
        Directory.CreateDirectory(path);
        return HeadlessStorageFolder.FromTempPath(path);
    }

    /// <summary>删除 <see cref="TempRoot"/> 及其内容；失败被忽略（临时文件清理不应让测试失败）。</summary>
    public void CleanupTempDirectory()
    {
        try
        {
            if (Directory.Exists(TempRoot)) Directory.Delete(TempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    string ResolveTempPath(string name)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (name.Length == 0) throw new ArgumentException("The temporary name cannot be empty.", nameof(name));
        string root = IOPath.GetFullPath(TempRoot);
        string full = IOPath.GetFullPath(IOPath.Combine(root, name));
        if (!IsUnder(root, full))
            throw new ArgumentException("The temporary path must stay under TempRoot.", nameof(name));
        Directory.CreateDirectory(root);
        return full;
    }

    static bool IsUnder(string root, string path)
    {
        root = root.TrimEnd(IOPath.DirectorySeparatorChar, IOPath.AltDirectorySeparatorChar);
        return path.Length > root.Length &&
               path.StartsWith(root, StringComparison.Ordinal) &&
               (path[root.Length] == IOPath.DirectorySeparatorChar || path[root.Length] == IOPath.AltDirectorySeparatorChar);
    }
}

/// <summary>
/// 一个文件句柄。两种形态：
/// <list type="bullet">
/// <item>纯内存（<see cref="IsMemoryBacked"/> 为 true）：<see cref="StorageFile.Path"/> 为空，读写走 <see cref="MemoryStream"/>，
/// 写入流在释放时把内容提交回文件对象。</item>
/// <item>临时路径：<see cref="StorageFile.Path"/> 为真实路径，读写走 <see cref="System.IO"/>。</item>
/// </list>
/// </summary>
public sealed class HeadlessStorageFile : StorageFile
{
    readonly string _name;
    readonly string? _path;
    byte[] _content;

    /// <summary>创建纯内存文件；<paramref name="content"/> 被复制。</summary>
    public HeadlessStorageFile(string name, byte[]? content = null)
    {
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _content = content is null ? Array.Empty<byte>() : (byte[])content.Clone();
    }

    /// <summary>创建纯内存文本文件，内容为 UTF-8 编码的 <paramref name="text"/>。</summary>
    public HeadlessStorageFile(string name, string text)
        : this(name, Encoding.UTF8.GetBytes(text ?? throw new ArgumentNullException(nameof(text))))
    {
    }

    HeadlessStorageFile(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _name = IOPath.GetFileName(path);
        _content = Array.Empty<byte>();
    }

    /// <summary>包装一个已有的临时路径（不要求文件当前存在）。</summary>
    public static HeadlessStorageFile FromTempPath(string path) => new(path);

    public override string Name => _name;

    /// <summary>纯内存文件返回空字符串（与 Web 上的平台文件对象一致）。</summary>
    public override string Path => _path ?? "";

    /// <summary>临时路径；纯内存文件为 null。</summary>
    public string? TempPath => _path;

    public bool IsMemoryBacked => _path is null;

    /// <summary>读取全部内容；临时文件不存在或不可读时返回空数组。</summary>
    public byte[] ReadAllBytes()
    {
        if (_path is null) return (byte[])_content.Clone();
        try
        {
            return File.ReadAllBytes(_path);
        }
        catch (IOException)
        {
            return Array.Empty<byte>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<byte>();
        }
    }

    /// <summary>整体写入内容（纯内存文件复制 <paramref name="content"/>）。</summary>
    public void WriteAllBytes(byte[] content)
    {
        if (content is null) throw new ArgumentNullException(nameof(content));
        if (_path is null) _content = (byte[])content.Clone();
        else File.WriteAllBytes(_path, content);
    }

    public override Task<Stream> OpenReadAsync()
    {
        if (_path is null)
            return Task.FromResult<Stream>(new MemoryStream(_content, writable: false));
        return Task.FromResult<Stream>(File.OpenRead(_path));
    }

    public override Task<Stream> OpenWriteAsync()
    {
        if (_path is null)
            return Task.FromResult<Stream>(new WriteBackStream(this));
        return Task.FromResult<Stream>(new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None));
    }

    public override Task<long> GetSizeAsync()
    {
        if (_path is null) return Task.FromResult((long)_content.Length);
        try
        {
            return Task.FromResult(new FileInfo(_path).Length);
        }
        catch (IOException)
        {
            return Task.FromResult(-1L);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(-1L);
        }
    }

    /// <summary>写入流在释放时把内容提交回纯内存文件（与文件流的语义一致）。</summary>
    sealed class WriteBackStream : MemoryStream
    {
        readonly HeadlessStorageFile _file;

        public WriteBackStream(HeadlessStorageFile file) => _file = file;

        protected override void Dispose(bool disposing)
        {
            if (disposing) _file._content = ToArray();
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// 一个目录句柄。纯内存目录用 <see cref="Files"/> / <see cref="Folders"/> 手动填充；
/// 临时路径目录按名称顺序（序数比较）枚举真实内容，目录不存在时返回空列表。
/// </summary>
public sealed class HeadlessStorageFolder : StorageFolder
{
    readonly string _name;
    readonly string? _path;
    readonly List<StorageFile> _files = new();
    readonly List<StorageFolder> _folders = new();

    /// <summary>创建纯内存目录。</summary>
    public HeadlessStorageFolder(string name)
    {
        _name = name ?? throw new ArgumentNullException(nameof(name));
    }

    HeadlessStorageFolder(string path, bool fromPath)
    {
        _ = fromPath;
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _name = new DirectoryInfo(path).Name;
    }

    /// <summary>包装一个已有的临时路径（不要求目录当前存在）。</summary>
    public static HeadlessStorageFolder FromTempPath(string path) => new(path, fromPath: true);

    public override string Name => _name;

    /// <summary>纯内存目录返回空字符串（与 Web 上的平台目录对象一致）。</summary>
    public override string Path => _path ?? "";

    /// <summary>临时路径；纯内存目录为 null。</summary>
    public string? TempPath => _path;

    public bool IsMemoryBacked => _path is null;

    /// <summary>纯内存目录的子文件（按添加顺序）。</summary>
    public IReadOnlyList<StorageFile> Files => _files;

    /// <summary>纯内存目录的子目录（按添加顺序）。</summary>
    public IReadOnlyList<StorageFolder> Folders => _folders;

    public void Add(StorageFile file)
    {
        if (file is null) throw new ArgumentNullException(nameof(file));
        _files.Add(file);
    }

    public void Add(StorageFolder folder)
    {
        if (folder is null) throw new ArgumentNullException(nameof(folder));
        _folders.Add(folder);
    }

    public bool Remove(StorageFile file) => _files.Remove(file);

    public bool Remove(StorageFolder folder) => _folders.Remove(folder);

    public override Task<IReadOnlyList<StorageFile>> GetFilesAsync()
    {
        if (_path is null) return Task.FromResult<IReadOnlyList<StorageFile>>(_files.ToArray());
        return Task.FromResult<IReadOnlyList<StorageFile>>(EnumerateFiles(_path));
    }

    public override Task<IReadOnlyList<StorageFolder>> GetFoldersAsync()
    {
        if (_path is null) return Task.FromResult<IReadOnlyList<StorageFolder>>(_folders.ToArray());
        return Task.FromResult<IReadOnlyList<StorageFolder>>(EnumerateFolders(_path));
    }

    static IReadOnlyList<StorageFile> EnumerateFiles(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return Array.Empty<StorageFile>();
            var paths = Directory.GetFiles(path);
            Array.Sort(paths, StringComparer.Ordinal);
            var result = new StorageFile[paths.Length];
            for (int i = 0; i < paths.Length; i++) result[i] = HeadlessStorageFile.FromTempPath(paths[i]);
            return result;
        }
        catch (IOException)
        {
            return Array.Empty<StorageFile>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<StorageFile>();
        }
    }

    static IReadOnlyList<StorageFolder> EnumerateFolders(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return Array.Empty<StorageFolder>();
            var paths = Directory.GetDirectories(path);
            Array.Sort(paths, StringComparer.Ordinal);
            var result = new StorageFolder[paths.Length];
            for (int i = 0; i < paths.Length; i++) result[i] = HeadlessStorageFolder.FromTempPath(paths[i]);
            return result;
        }
        catch (IOException)
        {
            return Array.Empty<StorageFolder>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<StorageFolder>();
        }
    }
}
