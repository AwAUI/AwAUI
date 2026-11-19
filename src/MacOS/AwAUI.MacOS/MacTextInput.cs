using System;
using System.Threading.Tasks;
using AwAUI.MacOS.Interop;
using AwAUI.Platform;
using static AwAUI.MacOS.Interop.ObjC;

namespace AwAUI.MacOS;

/// <summary>
/// macOS 的输入法桥（design/04「IME」）：窗口的视图遵循 <c>NSTextInputClient</c> 协议，按键在 keyDown: 中交给视图的
/// <c>NSTextInputContext</c>（<c>handleEvent:</c>，即 <c>interpretKeyEvents:</c> 的路径），输入法经 <c>setMarkedText:…</c> /
/// <c>insertText:…</c> / <c>unmarkText</c> 回调，这些调用被翻译成对 <see cref="TextInputMethod.Client"/> 的操作。
/// <list type="bullet">
/// <item>没有客户端（焦点不在可编辑元素上）或客户端是密码框时不经过输入法：按键的 <c>characters</c> 直接作为文本。</item>
/// <item>被输入法处理的按键（组合中，或这一键开始了组合）不交给框架的 <see cref="IWindowEvents.OnKey"/>，与浏览器一致。</item>
/// <item>候选窗位置：<c>firstRectForCharacterRange:</c> 查询客户端的文本矩形并换算成屏幕坐标；<see cref="NotifyStateChanged"/>
/// 调用 <c>invalidateCharacterCoordinates</c> 让输入法重新查询。</item>
/// <item><see cref="ResetComposition"/> 与切换客户端调用 <c>discardMarkedText</c>。</item>
/// </list>
/// </summary>
public sealed class MacTextInput : TextInputMethod
{
    readonly MacWindow _window;

    internal MacTextInput(MacWindow window) => _window = window;

    /// <summary>当前客户端是否走输入法（有客户端且不是密码框）。</summary>
    public bool UsesInputMethod => Client is { } c && !c.Options.IsPassword;

    protected override void OnClientChanged(ITextInputClient? oldClient, ITextInputClient? newClient)
    {
        Discard();
        Invalidate();
    }

    public override void NotifyStateChanged() => Invalidate();

    public override void ResetComposition() => Discard();

    void Discard()
    {
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
}
