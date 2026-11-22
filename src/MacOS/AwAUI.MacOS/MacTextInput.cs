using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AwAUI.Graphics;
using AwAUI.MacOS.Interop;
using AwAUI.Platform;
using static AwAUI.MacOS.Interop.ObjC;

namespace AwAUI.MacOS;

/// <summary>
/// macOS 的输入法桥（design/04「IME」）：窗口的视图遵循 <c>NSTextInputClient</c> 协议，按键在 keyDown: 中交给视图的
/// <c>NSTextInputContext</c>（<c>handleEvent:</c>，即 <c>interpretKeyEvents:</c> 的路径），输入法经 <c>setMarkedText:…</c> /
/// <c>insertText:…</c> / <c>unmarkText</c> 回调，这些调用被翻译成对 <see cref="TextInputMethod.Client"/> 的操作。
/// <para>
/// 没有客户端（焦点不在可编辑元素上）时不经过输入法：按键的 <c>characters</c> 直接作为文本。
/// 密码框仍保留输入法关联（否则无法输入中日韩文字与死键字符），预编辑文本由客户端遮罩。
/// </para>
/// <list type="bullet">
/// <item>被输入法处理的按键（组合中，或这一键开始了组合）不交给框架的 <see cref="IWindowEvents.OnKey"/>，与浏览器一致。</item>
/// <item>输入法的取消 / 提交 / 方向键命令经 <c>doCommandBySelector:</c> 到达，由 <see cref="MacWindow"/> 翻译成对客户端的操作。</item>
/// <item>候选窗位置：<c>firstRectForCharacterRange:</c> 查询客户端的文本矩形并换算成屏幕坐标；<see cref="NotifyStateChanged"/>
/// 调用 <c>invalidateCharacterCoordinates</c> 让输入法重新查询。</item>
/// <item><see cref="ResetComposition"/> 与切换客户端调用 <c>discardMarkedText</c>。</item>
/// </list>
/// </summary>
public sealed class MacTextInput : TextInputMethod
{
    readonly MacWindow _window;

    internal MacTextInput(MacWindow window) => _window = window;

    /// <summary>当前客户端是否走输入法（有客户端即交给输入法；密码框的预编辑由客户端遮罩）。</summary>
    public bool UsesInputMethod => Client is not null;

    protected override void OnClientChanged(ITextInputClient? oldClient, ITextInputClient? newClient)
    {
        Discard();
        Invalidate();
    }

    public override void NotifyStateChanged() => Invalidate();

    public override void ResetComposition() => Discard();

    void Discard()
    {
        // 密码框的预编辑只存在于平台侧（见 MacWindow 的 _markedText），切换客户端 / 程序性重置时一并清除
        _window.ClearMarkedText();
        var context = _window.InputContext;
        if (context == IntPtr.Zero) return;
        // 框架已经自行提交或丢弃了预编辑文本，而且 OnClientChanged 时 Client 已经是新客户端：输入法在 discardMarkedText 期间
        // 同步回调的 insertText / setMarkedText 都属于旧组合，忽略（否则会把旧预编辑再插入一次，甚至插进新的输入框）
        _window.DiscardingMarkedText = true;
        try
        {
            Send(context, Sel("discardMarkedText"));
        }
        finally
        {
            _window.DiscardingMarkedText = false;
        }
    }

    void Invalidate()
    {
        var context = _window.InputContext;
        if (context != IntPtr.Zero) Send(context, Sel("invalidateCharacterCoordinates"));
    }
}

/// <summary>
/// macOS 剪贴板：<c>NSPasteboard.generalPasteboard</c> 的纯文本（<c>NSPasteboardTypeString</c>）。读写同步完成；
/// AppKit 只能在主线程上访问，其他线程调用时投递到主线程。读取的文本超过 <see cref="Clipboard.MaxTextLength"/> 时截断。
/// </summary>
public sealed class MacClipboard : Clipboard
{
    readonly MacPlatform _platform;
    readonly string? _pasteboardName;

    /// <param name="platform">所属平台（主线程调度）。</param>
    /// <param name="pasteboardName">为 null 时用系统剪贴板（generalPasteboard）；否则用该名字的私有剪贴板（测试探针用，不碰用户的剪贴板）。</param>
    internal MacClipboard(MacPlatform platform, string? pasteboardName = null)
    {
        _platform = platform;
        _pasteboardName = pasteboardName;
    }

    const string PasteboardTypeString = "public.utf8-plain-text";
    const string PasteboardTypeHtml = "public.html";
    const string PasteboardTypePng = "public.png";
    const string PasteboardTypeTiff = "public.tiff";
    const string PasteboardTypeFilenames = "NSFilenamesPboardType"; // 旧格式：NSString 路径数组
    const int MaxPathLength = 4096;

    public override Task<string?> GetTextAsync() => OnMainThread(ReadText);

    IntPtr Pasteboard()
    {
        if (_pasteboardName is null) return Send(Class("NSPasteboard"), Sel("generalPasteboard"));
        var name = NewString(_pasteboardName);
        var pb = Send(Class("NSPasteboard"), Sel("pasteboardWithName:"), name);
        Release(name);
        return pb;
    }

    public override Task<bool> SetTextAsync(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        return OnMainThread(() => WriteText(LimitLength(text)));
    }

    Task<T> OnMainThread<T>(Func<T> action)
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

    string? ReadText()
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var pb = Pasteboard();
            if (pb == IntPtr.Zero) return null;
            var type = NewString(PasteboardTypeString);
            var s = Send(pb, Sel("stringForType:"), type);
            Release(type);
            var text = GetString(s, MaxTextLength);
            return string.IsNullOrEmpty(text) ? null : text;
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    bool WriteText(string text)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var pb = Pasteboard();
            if (pb == IntPtr.Zero) return false;
            Send(pb, Sel("clearContents"));
            var type = NewString(PasteboardTypeString);
            var s = NewString(text);
            bool ok = SendByte(pb, Sel("setString:forType:"), s, type) != 0;
            Release(s);
            Release(type);
            return ok;
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    // ---------------------------------------------------------------- 富剪贴板：HTML / 图片 / 文件

    public override Task<string?> GetHtmlAsync() => OnMainThread(ReadHtml);

    public override Task<bool> SetHtmlAsync(string html)
    {
        if (html is null) throw new ArgumentNullException(nameof(html));
        var text = LimitLength(html);
        return OnMainThread(() => WriteHtml(text));
    }

    public override Task<Bitmap?> GetImageAsync() => OnMainThread(ReadImage);

    public override Task<bool> SetImageAsync(Bitmap image)
    {
        if (image is null) throw new ArgumentNullException(nameof(image));
        return OnMainThread(() => WriteImage(image));
    }

    public override Task<IReadOnlyList<StorageFile>> GetFilesAsync() => OnMainThread(ReadFiles);

    public override Task<bool> SetFilesAsync(IReadOnlyList<StorageFile> files)
    {
        if (files is null) throw new ArgumentNullException(nameof(files));
        return OnMainThread(() => WriteFiles(files));
    }

    string? ReadHtml()
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var pb = Pasteboard();
            if (pb == IntPtr.Zero) return null;
            var type = NewString(PasteboardTypeHtml);
            var s = Send(pb, Sel("stringForType:"), type);
            Release(type);
            var html = GetString(s, MaxTextLength);
            return string.IsNullOrEmpty(html) ? null : html;
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    bool WriteHtml(string html)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var pb = Pasteboard();
            if (pb == IntPtr.Zero) return false;
            Send(pb, Sel("clearContents"));
            // 同时写纯文本回退：只接受纯文本的接收方得到去掉标签的可读文本
            var plain = NewString(LimitLength(MacHtml.ToPlainText(html)));
            var textType = NewString(PasteboardTypeString);
            bool textOk = SendByte(pb, Sel("setString:forType:"), plain, textType) != 0;
            Release(plain);
            Release(textType);

            var htmlString = NewString(html);
            var htmlType = NewString(PasteboardTypeHtml);
            bool htmlOk = SendByte(pb, Sel("setString:forType:"), htmlString, htmlType) != 0;
            Release(htmlString);
            Release(htmlType);
            return htmlOk || textOk;
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    Bitmap? ReadImage()
    {
        var pool = objc_autoreleasePoolPush();
        IntPtr rep = IntPtr.Zero;
        try
        {
            var pb = Pasteboard();
            if (pb == IntPtr.Zero) return null;
            var data = DataForType(pb, PasteboardTypePng);
            if (data == IntPtr.Zero) data = DataForType(pb, PasteboardTypeTiff);
            if (data == IntPtr.Zero) return null;

            rep = Send(Send(Class("NSBitmapImageRep"), Sel("alloc")), Sel("initWithData:"), data);
            if (rep == IntPtr.Zero) return null;
            var cg = Send(rep, Sel("CGImage"));
            if (cg == IntPtr.Zero) return null;

            int width = (int)Math.Min((ulong)CG.CGImageGetWidth(cg), int.MaxValue);
            int height = (int)Math.Min((ulong)CG.CGImageGetHeight(cg), int.MaxValue);
            if (width <= 0 || height <= 0) return null;

            var bitmap = new Bitmap(new PixelSize(width, height));
            var ctx = CG.CGBitmapContextCreate(bitmap.Address, (UIntPtr)(uint)width, (UIntPtr)(uint)height,
                (UIntPtr)8u, (UIntPtr)(uint)bitmap.RowBytes, MacWindow.SrgbColorSpace, CG.AlphaPremultipliedLast);
            if (ctx == IntPtr.Zero)
            {
                bitmap.Dispose();
                return null;
            }
            try
            {
                CG.CGContextSetBlendMode(ctx, CG.BlendModeCopy);
                CG.CGContextDrawImage(ctx, new NSRect(0, 0, width, height), cg);
            }
            finally { CG.CGContextRelease(ctx); }
            return bitmap;
        }
        finally
        {
            if (rep != IntPtr.Zero) Release(rep);
            objc_autoreleasePoolPop(pool);
        }
    }

    bool WriteImage(Bitmap image)
    {
        var pool = objc_autoreleasePoolPush();
        IntPtr rep = IntPtr.Zero;
        var cg = IntPtr.Zero;
        try
        {
            var pb = Pasteboard();
            if (pb == IntPtr.Zero) return false;
            cg = MacImages.CreateCGImage(image);
            if (cg == IntPtr.Zero) return false;
            rep = Send(Send(Class("NSBitmapImageRep"), Sel("alloc")), Sel("initWithCGImage:"), cg);
            if (rep == IntPtr.Zero) return false;

            var properties = Send(Class("NSDictionary"), Sel("dictionary"));
            var data = Send(rep, Sel("representationUsingType:properties:"), 4L /* NSBitmapImageFileTypePNG */, properties);
            if (data == IntPtr.Zero) return false;

            Send(pb, Sel("clearContents"));
            var type = NewString(PasteboardTypePng);
            bool ok = SendByte(pb, Sel("setData:forType:"), data, type) != 0;
            Release(type);
            return ok;
        }
        finally
        {
            if (rep != IntPtr.Zero) Release(rep);
            if (cg != IntPtr.Zero) CG.CGImageRelease(cg);
            objc_autoreleasePoolPop(pool);
        }
    }

    IReadOnlyList<StorageFile> ReadFiles()
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var pb = Pasteboard();
            if (pb == IntPtr.Zero) return Array.Empty<StorageFile>();
            var paths = new List<string>();

            // 旧格式：NSFilenamesPboardType 是 NSString 路径数组
            var legacyType = NewString(PasteboardTypeFilenames);
            var plist = Send(pb, Sel("propertyListForType:"), legacyType);
            Release(legacyType);
            if (plist != IntPtr.Zero && SendByte(plist, Sel("isKindOfClass:"), Class("NSArray")) != 0)
                AddPaths(plist, paths);

            // 现代格式：文件 URL 对象
            if (paths.Count == 0)
            {
                var classes = Send(Class("NSArray"), Sel("arrayWithObject:"), Class("NSURL"));
                var options = Send(Class("NSDictionary"), Sel("dictionary"));
                var urls = Send(pb, Sel("readObjectsForClasses:options:"), classes, options);
                if (urls != IntPtr.Zero) AddPaths(urls, paths);
            }

            var files = new List<StorageFile>(paths.Count);
            foreach (var path in paths)
            {
                var normalized = MacStoragePaths.Normalize(path);
                if (MacStoragePaths.Exists(normalized, directory: false)) files.Add(new MacStorageFile(normalized!));
            }
            return files;
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    bool WriteFiles(IReadOnlyList<StorageFile> files)
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var pb = Pasteboard();
            if (pb == IntPtr.Zero) return false;
            var urls = Send(Class("NSMutableArray"), Sel("array"));
            foreach (var file in files)
            {
                var path = MacStoragePaths.Normalize(file?.Path);
                if (path is null) continue;
                var ns = NewString(path);
                var url = Send(Class("NSURL"), Sel("fileURLWithPath:"), ns);
                Release(ns);
                if (url != IntPtr.Zero) Send(urls, Sel("addObject:"), url);
            }
            Send(pb, Sel("clearContents"));
            if (SendULong(urls, Sel("count")) == 0) return true;
            return SendByte(pb, Sel("writeObjects:"), urls) != 0;
        }
        finally { objc_autoreleasePoolPop(pool); }
    }

    static IntPtr DataForType(IntPtr pasteboard, string typeName)
    {
        var type = NewString(typeName);
        var data = Send(pasteboard, Sel("dataForType:"), type);
        Release(type);
        return data;
    }

    static void AddPaths(IntPtr array, List<string> paths)
    {
        int count = array == IntPtr.Zero ? 0 : (int)Math.Min(SendULong(array, Sel("count")), int.MaxValue);
        for (int i = 0; i < count; i++)
        {
            var item = Send(array, Sel("objectAtIndex:"), (long)i);
            if (item == IntPtr.Zero) continue;
            string? path;
            if (SendByte(item, Sel("isKindOfClass:"), Class("NSString")) != 0)
                path = GetString(item, MaxPathLength);
            else if (SendByte(item, Sel("isKindOfClass:"), Class("NSURL")) != 0)
                path = GetString(Send(item, Sel("path")), MaxPathLength);
            else continue;
            if (!string.IsNullOrEmpty(path)) paths.Add(path!);
        }
    }
}
