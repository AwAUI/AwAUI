using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AwAUI.Platform;
using AwAUI.Win32.Interop;
using static AwAUI.Win32.Interop.Const;

namespace AwAUI.Win32;

/// <summary>
/// 文件与目录选择器（design/04「文件对话框」）。用经典 comdlg32 的 GetOpenFileNameW / GetSaveFileNameW
/// （多选经 OFN_ALLOWMULTISELECT + 双 NUL 缓冲）与 shell32 的 SHBrowseForFolderW；对话框在 UI 线程打开，
/// 返回的任务在其它线程完成。所有路径都经过绝对路径与长度/控制字符校验，坏输入返回 null / 空列表而不是抛异常。
/// </summary>
public sealed class Win32StorageProvider : StorageProvider
{
    /// <summary>打开对话框的多选缓冲区（字符数，UTF-16）。经典对话框的上限是 64K，对齐这一上限。</summary>
    const int OpenBufferChars = 65536;
    /// <summary>保存对话框缓冲区。</summary>
    const int SaveBufferChars = 32768;

    readonly Win32Platform _platform;

    internal Win32StorageProvider(Win32Platform platform) => _platform = platform;

    public override Task<IReadOnlyList<StorageFile>> OpenFilesAsync(FilePickerOptions options) =>
        OnUiThread(() => OpenFiles(options ?? new FilePickerOptions()));

    public override Task<StorageFile?> SaveFileAsync(FilePickerOptions options) =>
        OnUiThread(() => SaveFile(options ?? new FilePickerOptions()));

    public override Task<StorageFolder?> OpenFolderAsync(FilePickerOptions options) =>
        OnUiThread(() => OpenFolder(options ?? new FilePickerOptions()));

    public override Task<StorageFile?> TryGetFileFromPathAsync(string path)
    {
        if (!Win32Path.IsSafeAbsolutePath(path)) return Task.FromResult<StorageFile?>(null);
        return Task.Run<StorageFile?>(() =>
        {
            try { return File.Exists(path) ? new Win32StorageFile(path) : null; }
            catch (Exception e) when (e is not OutOfMemoryException) { return null; }
        });
    }

    public override Task<StorageFolder?> TryGetFolderFromPathAsync(string path)
    {
        if (!Win32Path.IsSafeAbsolutePath(path)) return Task.FromResult<StorageFolder?>(null);
        return Task.Run<StorageFolder?>(() =>
        {
            try { return Directory.Exists(path) ? new Win32StorageFolder(path) : null; }
            catch (Exception e) when (e is not OutOfMemoryException) { return null; }
        });
    }

    unsafe IReadOnlyList<StorageFile> OpenFiles(FilePickerOptions options)
    {
        var buffer = new char[OpenBufferChars];
        string filter = Win32Dialogs.BuildFilter(options.FileTypes);
        string title = options.Title ?? "Open";
        var owner = Owner();
        bool multiple = options.AllowMultiple;

        fixed (char* pFile = buffer)
        fixed (char* pFilter = filter)
        fixed (char* pTitle = title)
        {
            var ofn = new OPENFILENAMEW
            {
                lStructSize = (uint)sizeof(OPENFILENAMEW),
                hwndOwner = owner,
                lpstrFilter = pFilter,
                nFilterIndex = 1,
                lpstrFile = pFile,
                nMaxFile = (uint)buffer.Length,
                lpstrTitle = pTitle,
                Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR |
                        (multiple ? OFN_ALLOWMULTISELECT : 0),
            };
            try
            {
                if (Comdlg32.GetOpenFileNameW(&ofn) == 0) return Array.Empty<StorageFile>();
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return Array.Empty<StorageFile>();
            }
        }
        return ToFiles(Win32Dialogs.ParseOpenPaths(buffer, multiple));
    }

    static IReadOnlyList<StorageFile> ToFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return Array.Empty<StorageFile>();
        var files = new List<StorageFile>(paths.Count);
        foreach (string path in paths) files.Add(new Win32StorageFile(path));
        return files;
    }

    unsafe StorageFile? SaveFile(FilePickerOptions options)
    {
        var buffer = new char[SaveBufferChars];
        string filter = Win32Dialogs.BuildFilter(options.FileTypes);
        string title = options.Title ?? "Save";
        string suggested = SuggestedName(options.SuggestedFileName);
        string? extension = DefaultExtension(options.DefaultExtension);
        if (suggested.Length > 0) suggested.CopyTo(0, buffer, 0, Math.Min(suggested.Length, buffer.Length - 1));
        var owner = Owner();

        fixed (char* pFile = buffer)
        fixed (char* pFilter = filter)
        fixed (char* pTitle = title)
        {
            try
            {
                if (extension is not null)
                {
                    fixed (char* e = extension)
                    {
                        if (!SaveDialog(pFile, pFilter, pTitle, e, owner)) return null;
                    }
                }
                else if (!SaveDialog(pFile, pFilter, pTitle, null, owner))
                {
                    return null;
                }
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
        }

        int end = IndexOfNul(buffer, 0);
        if (end <= 0) return null;
        string path = new string(buffer, 0, end);
        return Win32Path.IsSafeAbsolutePath(path) ? new Win32StorageFile(path) : null;
    }

    /// <summary>调用 GetSaveFileNameW；取消/失败返回 false。指针参数已由调用方固定。</summary>
    static unsafe bool SaveDialog(char* pFile, char* pFilter, char* pTitle, char* pExt, IntPtr owner)
    {
        var ofn = new OPENFILENAMEW
        {
            lStructSize = (uint)sizeof(OPENFILENAMEW),
            hwndOwner = owner,
            lpstrFilter = pFilter,
            nFilterIndex = 1,
            lpstrFile = pFile,
            nMaxFile = SaveBufferChars,
            lpstrTitle = pTitle,
            lpstrDefExt = pExt,
            Flags = OFN_EXPLORER | OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR,
        };
        return Comdlg32.GetSaveFileNameW(&ofn) != 0;
    }

    unsafe StorageFolder? OpenFolder(FilePickerOptions options)
    {
        var display = new char[260];
        var pathBuffer = new char[Win32Path.MaxPath];
        string title = options.Title ?? "Select folder";
        var owner = Owner();

        fixed (char* pDisplay = display)
        fixed (char* pTitle = title)
        {
            var info = new BROWSEINFOW
            {
                hwndOwner = owner,
                pszDisplayName = pDisplay,
                lpszTitle = pTitle,
                ulFlags = BIF_RETURNONLYFSDIRS | BIF_USENEWUI,
            };
            IntPtr pidl;
            try
            {
                pidl = Shell32.SHBrowseForFolderW(&info);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
            if (pidl == IntPtr.Zero) return null;
            try
            {
                fixed (char* pPath = pathBuffer)
                {
                    if (Shell32.SHGetPathFromIDListW(pidl, pPath) == 0) return null;
                    int end = IndexOfNul(pathBuffer, 0);
                    if (end <= 0) return null;
                    string path = new string(pathBuffer, 0, end);
                    return Win32Path.IsSafeAbsolutePath(path) ? new Win32StorageFolder(path) : null;
                }
            }
            finally
            {
                Ole32.CoTaskMemFree(pidl);
            }
        }
    }

    static int IndexOfNul(char[] buffer, int start) =>
        start < 0 || start >= buffer.Length ? -1 : Array.IndexOf(buffer, '\0', start);

    static string SuggestedName(string? name)
    {
        if (name is null || name.Length == 0) return "";
        int nul = name.IndexOf('\0');
        if (nul >= 0) name = name.Substring(0, nul);
        for (int i = 0; i < name.Length; i++)
            if (char.IsControl(name[i])) return "";
        try { name = Path.GetFileName(name); }
        catch (Exception) { return ""; }
        return name is null ? "" : name.Length > 255 ? name.Substring(0, 255) : name;
    }

    static string? DefaultExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension)) return null;
        string value = extension!.Trim().TrimStart('.');
        if (value.Length == 0 || value.Length > 64) return null;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (!(char.IsLetterOrDigit(c) || c is '_' or '-' or '.')) return null;
        }
        return value;
    }

    IntPtr Owner()
    {
        var hwnd = User32.GetActiveWindow();
        return hwnd != IntPtr.Zero ? hwnd : _platform.MessageWindow;
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

/// <summary>System.IO 之上的文件句柄。</summary>
internal sealed class Win32StorageFile : StorageFile
{
    public Win32StorageFile(string path)
    {
        Path = path;
        Name = GetName(path);
    }

    public override string Name { get; }
    public override string Path { get; }

    static string GetName(string path)
    {
        try
        {
            string name = System.IO.Path.GetFileName(path);
            return name.Length == 0 ? path : name;
        }
        catch (Exception)
        {
            return path;
        }
    }

    public override Task<Stream> OpenReadAsync() =>
        Task.Run<Stream>(() => new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read));

    public override Task<Stream> OpenWriteAsync() =>
        Task.Run<Stream>(() => new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.None));

    public override Task<long> GetSizeAsync() => Task.Run(() =>
    {
        try
        {
            var info = new FileInfo(Path);
            return info.Exists ? info.Length : -1L;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return -1L;
        }
    });
}

/// <summary>System.IO 之上的目录句柄。</summary>
internal sealed class Win32StorageFolder : StorageFolder
{
    public Win32StorageFolder(string path)
    {
        Path = path;
        Name = GetName(path);
    }

    public override string Name { get; }
    public override string Path { get; }

    static string GetName(string path)
    {
        try
        {
            string trimmed = path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            string name = System.IO.Path.GetFileName(trimmed);
            return name.Length == 0 ? path : name;
        }
        catch (Exception)
        {
            return path;
        }
    }

    public override Task<IReadOnlyList<StorageFile>> GetFilesAsync() => Task.Run<IReadOnlyList<StorageFile>>(() =>
    {
        var files = new List<StorageFile>();
        try
        {
            foreach (string path in Directory.EnumerateFiles(Path))
            {
                if (files.Count >= 100000) break;
                files.Add(new Win32StorageFile(path));
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
        }
        return files;
    });

    public override Task<IReadOnlyList<StorageFolder>> GetFoldersAsync() => Task.Run<IReadOnlyList<StorageFolder>>(() =>
    {
        var folders = new List<StorageFolder>();
        try
        {
            foreach (string path in Directory.EnumerateDirectories(Path))
            {
                if (folders.Count >= 100000) break;
                folders.Add(new Win32StorageFolder(path));
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
        }
        return folders;
    });
}
