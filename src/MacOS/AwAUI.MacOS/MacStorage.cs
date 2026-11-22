using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AwAUI.Platform;
using static AwAUI.MacOS.Interop.ObjC;

namespace AwAUI.MacOS;

/// <summary>
/// macOS 文件与目录选择器：<c>NSOpenPanel</c> / <c>NSSavePanel</c>，结果映射为 <see cref="StorageFile"/> / <see cref="StorageFolder"/>。
/// 面板以 sheet（没有 key window 时用 modeless）方式开始，完成后在 UI 线程上把路径交给调用方，不阻塞主消息循环。
/// </summary>
internal sealed class MacStorageProvider : StorageProvider
{
    readonly MacPlatform _platform;

    internal MacStorageProvider(MacPlatform platform) => _platform = platform;

    public override async Task<IReadOnlyList<StorageFile>> OpenFilesAsync(FilePickerOptions options)
    {
        options ??= new FilePickerOptions();
        var paths = await OnMain(() => BeginPanel(save: false, folder: false, options)).ConfigureAwait(false);
        var files = new List<StorageFile>(paths.Count);
        foreach (var path in paths) files.Add(new MacStorageFile(path));
        return files;
    }

    public override async Task<StorageFile?> SaveFileAsync(FilePickerOptions options)
    {
        options ??= new FilePickerOptions();
        var paths = await OnMain(() => BeginPanel(save: true, folder: false, options)).ConfigureAwait(false);
        return paths.Count > 0 ? new MacStorageFile(paths[0]) : null;
    }

    public override async Task<StorageFolder?> OpenFolderAsync(FilePickerOptions options)
    {
        options ??= new FilePickerOptions();
        var paths = await OnMain(() => BeginPanel(save: false, folder: true, options)).ConfigureAwait(false);
        return paths.Count > 0 ? new MacStorageFolder(paths[0]) : null;
    }

    public override Task<StorageFile?> TryGetFileFromPathAsync(string path)
    {
        var normalized = MacStoragePaths.Normalize(path);
        return Task.FromResult<StorageFile?>(MacStoragePaths.Exists(normalized, directory: false) ? new MacStorageFile(normalized!) : null);
    }

    public override Task<StorageFolder?> TryGetFolderFromPathAsync(string path)
    {
        var normalized = MacStoragePaths.Normalize(path);
        return Task.FromResult<StorageFolder?>(MacStoragePaths.Exists(normalized, directory: true) ? new MacStorageFolder(normalized!) : null);
    }

    /// <summary>把面板的开始 / 完成投递到 UI 线程；其他线程调用时等待结果，不直接触碰 AppKit。</summary>
    async Task<IReadOnlyList<string>> OnMain(Func<Task<IReadOnlyList<string>>> starter)
    {
        if (_platform.Dispatcher.CheckAccess()) return await starter().ConfigureAwait(false);
        var tcs = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _platform.Dispatcher.Post(() =>
        {
            try
            {
                starter().ContinueWith(t =>
                {
                    if (t.IsFaulted) tcs.TrySetException(t.Exception!.InnerExceptions);
                    else if (t.IsCanceled) tcs.TrySetCanceled();
                    else tcs.TrySetResult(t.Result);
                }, TaskScheduler.Default);
            }
            catch (Exception e) when (e is not OutOfMemoryException) { tcs.TrySetException(e); }
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- 面板

    Task<IReadOnlyList<string>> BeginPanel(bool save, bool folder, FilePickerOptions options)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pool = objc_autoreleasePoolPush();
        IntPtr panel = IntPtr.Zero, del = IntPtr.Zero;
        GCHandle context = default;
        try
        {
            panel = Retain(Send(Class(save ? "NSSavePanel" : "NSOpenPanel"), Sel(save ? "savePanel" : "openPanel")));
            if (panel == IntPtr.Zero)
            {
                tcs.TrySetResult(Array.Empty<string>());
                return tcs.Task;
            }
            ConfigurePanel(panel, save, folder, options);

            del = Retain(Send(Send(Class("AwAUIStorageDelegate"), Sel("alloc")), Sel("init")));
            var request = new PanelRequest(tcs, panel, del, save);
            context = GCHandle.Alloc(request);

            if (!BeginPanelSession(panel, del, GCHandle.ToIntPtr(context)))
            {
                // 系统上没有可用的非阻塞开始方法：取消而不是用 runModal 阻塞消息循环。
                context.Free();
                Release(panel);
                Release(del);
                tcs.TrySetResult(Array.Empty<string>());
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (context.IsAllocated) context.Free();
            if (panel != IntPtr.Zero) Release(panel);
            if (del != IntPtr.Zero) Release(del);
            tcs.TrySetException(e);
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
        return tcs.Task;
    }

    static void ConfigurePanel(IntPtr panel, bool save, bool folder, FilePickerOptions options)
    {
        if (!string.IsNullOrEmpty(options.Title))
        {
            var title = NewString(options.Title!);
            Send(panel, Sel("setTitle:"), title);
            Release(title);
        }

        if (save)
        {
            SendBool(panel, Sel("setCanCreateDirectories:"), 1);
            SendBool(panel, Sel("setExtensionHidden:"), 0);
            SendBool(panel, Sel("setCanChooseFiles:"), 1);
            SendBool(panel, Sel("setCanChooseDirectories:"), 0);
            var name = SuggestedName(options);
            if (name is not null)
            {
                var ns = NewString(name);
                Send(panel, Sel("setNameFieldStringValue:"), ns);
                Release(ns);
            }
        }
        else
        {
            SendBool(panel, Sel("setCanChooseFiles:"), folder ? (byte)0 : (byte)1);
            SendBool(panel, Sel("setCanChooseDirectories:"), folder ? (byte)1 : (byte)0);
            SendBool(panel, Sel("setAllowsMultipleSelection:"), folder || !options.AllowMultiple ? (byte)0 : (byte)1);
            SendBool(panel, Sel("setResolvesAliases:"), 1);
        }

        if (!folder) ApplyFileTypes(panel, options);
    }

    static string? SuggestedName(FilePickerOptions options)
    {
        string? name = options.SuggestedFileName;
        if (string.IsNullOrEmpty(name)) return null;
        string ext = CleanExtension(options.DefaultExtension) ?? "";
        if (ext.Length > 0 && !name!.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase)) name += "." + ext;
        return name;
    }

    static void ApplyFileTypes(IntPtr panel, FilePickerOptions options)
    {
        var utis = UniformTypeIdentifiers(options);
        if (utis.Count > 0 && ApplyContentTypes(panel, utis)) return;

        var extensions = Extensions(options);
        if (extensions.Count == 0) return;
        var array = Send(Class("NSMutableArray"), Sel("array"));
        foreach (var extension in extensions)
        {
            var s = NewString(extension);
            Send(array, Sel("addObject:"), s);
            Release(s);
        }
        Send(panel, Sel("setAllowedFileTypes:"), array);
    }

    static bool ApplyContentTypes(IntPtr panel, List<string> utis)
    {
        if (SendByte(panel, Sel("respondsToSelector:"), Sel("setAllowedContentTypes:")) == 0) return false;
        var typeClass = TryClass("UTType");
        if (typeClass == IntPtr.Zero)
        {
            try
            {
                Interop.CG.LoadFramework("/System/Library/Frameworks/UniformTypeIdentifiers.framework/UniformTypeIdentifiers");
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            typeClass = TryClass("UTType");
            if (typeClass == IntPtr.Zero) return false;
        }

        var array = Send(Class("NSMutableArray"), Sel("array"));
        foreach (var id in utis)
        {
            var ns = NewString(id);
            var type = Send(typeClass, Sel("typeWithIdentifier:"), ns);
            Release(ns);
            if (type != IntPtr.Zero) Send(array, Sel("addObject:"), type);
        }
        if (SendULong(array, Sel("count")) == 0) return false;
        Send(panel, Sel("setAllowedContentTypes:"), array);
        return true;
    }

    static List<string> UniformTypeIdentifiers(FilePickerOptions options)
    {
        var list = new List<string>();
        foreach (var fileType in options.FileTypes)
        {
            if (fileType is null) continue;
            foreach (var id in fileType.AppleUniformTypeIdentifiers)
                if (MacStoragePaths.IsValidUniformTypeIdentifier(id) && !list.Contains(id)) list.Add(id);
        }
        return list;
    }

    static List<string> Extensions(FilePickerOptions options)
    {
        var list = new List<string>();
        foreach (var fileType in options.FileTypes)
        {
            if (fileType is null) continue;
            foreach (var pattern in fileType.Patterns)
            {
                int dot = pattern?.LastIndexOf('.') ?? -1;
                if (dot < 0 || dot == pattern!.Length - 1) continue;
                var extension = CleanExtension(pattern.Substring(dot + 1));
                if (extension is not null && !list.Contains(extension)) list.Add(extension);
            }
        }
        var fallback = CleanExtension(options.DefaultExtension);
        if (fallback is not null && !list.Contains(fallback)) list.Add(fallback);
        return list;
    }

    static string? CleanExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension)) return null;
        var value = extension!.Trim().TrimStart('.');
        if (value.Length == 0 || value.Length > 64) return null;
        foreach (char c in value)
            if (c == '/' || c == '\\' || c == '*' || c == '?' || c < 0x20 || c == 0x7F) return null;
        return value;
    }

    static bool BeginPanelSession(IntPtr panel, IntPtr del, IntPtr context)
    {
        var endSelector = Sel("awPanelDidEnd:returnCode:contextInfo:");
        var app = Send(Class("NSApplication"), Sel("sharedApplication"));
        var window = Send(app, Sel("keyWindow"));
        if (window == IntPtr.Zero) window = Send(app, Sel("mainWindow"));

        var sheet = Sel("beginSheetForDirectory:file:types:modalForWindow:modalDelegate:didEndSelector:contextInfo:");
        if (window != IntPtr.Zero && SendByte(panel, Sel("respondsToSelector:"), sheet) != 0)
        {
            SendVoid(panel, sheet, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, window, del, endSelector, context);
            return true;
        }
        // 没有窗口（或系统移除了 sheet 版本）时用非阻塞的 modeless 面板
        var modeless = Sel("beginForDirectory:file:types:modelessDelegate:didEndSelector:contextInfo:");
        if (SendByte(panel, Sel("respondsToSelector:"), modeless) != 0)
        {
            SendVoid(panel, modeless, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, del, endSelector, context);
            return true;
        }
        return false;
    }

    /// <summary>面板完成的原生回调（主线程）：读取选中路径并在 UI 线程上完成任务。</summary>
    internal static void OnPanelEnd(IntPtr panel, long returnCode, IntPtr contextInfo)
    {
        if (contextInfo == IntPtr.Zero) return;
        PanelRequest? request = null;
        try
        {
            var handle = GCHandle.FromIntPtr(contextInfo);
            request = handle.Target as PanelRequest;
            handle.Free();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        if (request is null) return;

        // NSModalResponseOK = 1；取消（0）与其它响应都按取消处理
        request.Completion.TrySetResult(returnCode == 1 ? ReadPaths(request.Panel, request.Save) : Array.Empty<string>());
        Release(request.Panel);
        Release(request.Delegate);
    }

    static string[] ReadPaths(IntPtr panel, bool save)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var paths = new List<string>();
            if (save)
            {
                var path = MacStoragePaths.Normalize(PathOf(Send(panel, Sel("URL"))));
                if (path is not null) paths.Add(path);
            }
            else
            {
                var urls = Send(panel, Sel("URLs"));
                int count = urls == IntPtr.Zero ? 0 : (int)Math.Min(SendULong(urls, Sel("count")), int.MaxValue);
                for (int i = 0; i < count; i++)
                {
                    var path = MacStoragePaths.Normalize(PathOf(Send(urls, Sel("objectAtIndex:"), (long)i)));
                    if (path is not null) paths.Add(path);
                }
            }
            return paths.ToArray();
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    static string? PathOf(IntPtr url) =>
        url == IntPtr.Zero ? null : GetString(Send(url, Sel("path")), MacStoragePaths.MaxPathLength);

    sealed class PanelRequest
    {
        public readonly TaskCompletionSource<IReadOnlyList<string>> Completion;
        public readonly IntPtr Panel;
        public readonly IntPtr Delegate;
        public readonly bool Save;

        public PanelRequest(TaskCompletionSource<IReadOnlyList<string>> completion, IntPtr panel, IntPtr @delegate, bool save)
        {
            Completion = completion;
            Panel = panel;
            Delegate = @delegate;
            Save = save;
        }
    }
}

/// <summary>macOS 上的文件句柄：路径是面板返回的绝对路径，读写流由 .NET 文件 API 打开。</summary>
internal sealed class MacStorageFile : StorageFile
{
    public MacStorageFile(string path)
    {
        Path = path;
        var name = System.IO.Path.GetFileName(path);
        Name = name.Length > 0 ? name : path;
    }

    public override string Name { get; }
    public override string Path { get; }

    public override Task<Stream> OpenReadAsync() =>
        Task.FromResult<Stream>(new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read));

    public override Task<Stream> OpenWriteAsync()
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory!);
        return Task.FromResult<Stream>(new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.None));
    }

    public override Task<long> GetSizeAsync()
    {
        try
        {
            var info = new FileInfo(Path);
            return Task.FromResult(info.Exists ? info.Length : -1L);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(-1L);
        }
    }
}

/// <summary>macOS 上的目录句柄：枚举直接子项，读不到时返回空列表而不是抛异常。</summary>
internal sealed class MacStorageFolder : StorageFolder
{
    public MacStorageFolder(string path)
    {
        Path = path;
        var name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
        Name = name.Length > 0 ? name : path;
    }

    public override string Name { get; }
    public override string Path { get; }

    public override Task<IReadOnlyList<StorageFile>> GetFilesAsync()
    {
        var files = new List<StorageFile>();
        foreach (var path in MacStoragePaths.Contents(Path, directories: false)) files.Add(new MacStorageFile(path));
        return Task.FromResult<IReadOnlyList<StorageFile>>(files);
    }

    public override Task<IReadOnlyList<StorageFolder>> GetFoldersAsync()
    {
        var folders = new List<StorageFolder>();
        foreach (var path in MacStoragePaths.Contents(Path, directories: true)) folders.Add(new MacStorageFolder(path));
        return Task.FromResult<IReadOnlyList<StorageFolder>>(folders);
    }
}

/// <summary>StorageProvider 的路径校验、存在性与目录枚举（经 NSFileManager）。</summary>
internal static unsafe class MacStoragePaths
{
    /// <summary>单个路径的最大长度（macOS 的 PATH_MAX 是 1024；留出余量用于兼容挂载点与代理）。</summary>
    public const int MaxPathLength = 4096;

    /// <summary>路径可用时返回原字符串，否则返回 null；相对路径和控制字符一律拒绝。</summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path) || path!.Length > MaxPathLength) return null;
        foreach (char c in path)
            if (c < 0x20 || c == 0x7F) return null;
        try
        {
            return System.IO.Path.IsPathRooted(path) ? path : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>经 <c>NSFileManager fileExistsAtPath:isDirectory:</c> 判断路径存在且类型匹配。线程安全，可在任意线程调用。</summary>
    public static bool Exists(string? path, bool directory)
    {
        var normalized = Normalize(path);
        if (normalized is null) return false;
        var pool = objc_autoreleasePoolPush();
        try
        {
            var ns = NewString(normalized);
            byte isDirectory = 0;
            bool exists = SendByte(Send(Class("NSFileManager"), Sel("defaultManager")),
                Sel("fileExistsAtPath:isDirectory:"), ns, (IntPtr)(&isDirectory)) != 0;
            Release(ns);
            return exists && (isDirectory != 0) == directory;
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary>经 <c>NSFileManager contentsOfDirectoryAtPath:error:</c> 枚举直接子项（不递归）；失败时返回空数组。</summary>
    public static string[] Contents(string directory, bool directories)
    {
        var root = Normalize(directory);
        if (root is null) return Array.Empty<string>();
        var pool = objc_autoreleasePoolPush();
        try
        {
            var ns = NewString(root);
            var list = Send(Send(Class("NSFileManager"), Sel("defaultManager")),
                Sel("contentsOfDirectoryAtPath:error:"), ns, IntPtr.Zero);
            Release(ns);
            if (list == IntPtr.Zero) return Array.Empty<string>();

            int count = (int)Math.Min(SendULong(list, Sel("count")), int.MaxValue);
            var result = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                var name = GetString(Send(list, Sel("objectAtIndex:"), (long)i), 256);
                if (string.IsNullOrEmpty(name)) continue;
                string path;
                try { path = System.IO.Path.Combine(root, name!); }
                catch (ArgumentException) { continue; }
                if (Exists(path, directories)) result.Add(path);
            }
            return result.ToArray();
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    /// <summary>UTI 只能是 ASCII 字母、数字、点、连字符与下划线（如 public.png、com.adobe.pdf）。</summary>
    public static bool IsValidUniformTypeIdentifier(string? id)
    {
        if (string.IsNullOrEmpty(id) || id!.Length > 256) return false;
        foreach (char c in id)
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '_'))
                return false;
        return true;
    }
}
