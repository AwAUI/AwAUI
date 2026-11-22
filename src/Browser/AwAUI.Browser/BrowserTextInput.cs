using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AwAUI.Browser.Interop;
using AwAUI.Graphics;
using AwAUI.Platform;

namespace AwAUI.Browser;

/// <summary>
/// 浏览器上的输入法桥（design/04「IME」，<see cref="TextInputMethod"/> 的 Web 实现）：canvas 不能直接接收输入法，键盘焦点实际落在一个
/// 隐藏的 &lt;textarea&gt; 上，它的 compositionstart / compositionupdate / compositionend 被整理成干净的序列
/// （<see cref="CompositionChanged"/>），并交给当前的 <see cref="TextInputMethod.Client"/>：更新 → <see cref="ITextInputClient.SetComposition"/>，
/// 提交 → <see cref="ITextInputClient.Commit"/>，取消 → 空的 SetComposition。没有客户端时提交的文本经
/// <see cref="IWindowEvents.OnTextInput"/> 送达。
/// <list type="bullet">
/// <item>候选窗：<see cref="NotifyStateChanged"/> 查询客户端的插入点矩形并把代理文本框移过去（<see cref="SetCaretRect"/>）。</item>
/// <item>剪贴板快捷键由浏览器的 copy / cut / paste 事件完成（<see cref="HandlesClipboardShortcuts"/>）：平台经客户端取选中文本、删除或替换，
/// 不需要剪贴板权限。</item>
/// <item>密码框：代理是 textarea，浏览器不会像对真正的密码框那样关闭输入法；客户端忽略密码框的预编辑，提交的文本照常插入。</item>
/// </list>
/// </summary>
public sealed class BrowserTextInput : TextInputMethod
{
    readonly BrowserWindow _owner;
    readonly CompositionTracker _tracker;
    bool _enabled;

    internal BrowserTextInput(BrowserWindow owner, CompositionTracker tracker)
    {
        _owner = owner;
        _tracker = tracker;
    }

    /// <summary>输入法正在组合（预编辑）。</summary>
    public bool IsComposing => _tracker.IsComposing;

    /// <summary>当前组合文本及其中的光标 / 选区；没有组合时为空串。</summary>
    public string CompositionText => _tracker.Text;

    /// <inheritdoc cref="CompositionText"/>
    public int CompositionSelectionStart => _tracker.SelectionStart;

    /// <inheritdoc cref="CompositionText"/>
    public int CompositionSelectionEnd => _tracker.SelectionEnd;

    /// <summary>可编辑元素是否获得了焦点（<see cref="Show"/> 之后为 true）。</summary>
    public bool IsEnabled => _enabled;

    /// <summary>输入法组合的开始、更新与结束（与是否有客户端无关，诊断与自绘编辑框用）。</summary>
    public event Action<TextCompositionEvent>? CompositionChanged;

    /// <summary>浏览器的 copy / cut / paste 事件把剪贴板快捷键变成对客户端的操作，框架不再处理它们。</summary>
    public override bool HandlesClipboardShortcuts => true;

    protected override void OnClientChanged(ITextInputClient? oldClient, ITextInputClient? newClient)
    {
        // 切换客户端：放弃旧客户端上平台侧进行中的组合（框架在此之前已经按需提交了预编辑文本）
        if (_tracker.IsComposing) ResetComposition();
        if (newClient is null && _enabled) Hide();
    }

    /// <summary>客户端的插入点变化：代理文本框移到插入点，候选窗随之定位。</summary>
    public override void NotifyStateChanged()
    {
        if (Client is { } c) SetCaretRect(c.GetCaretRect());
    }

    /// <summary>放弃进行中的组合：代理文本框失焦再聚焦（期间的焦点与组合事件被 JS 胶水忽略），跟踪器回到空闲。</summary>
    public override void ResetComposition()
    {
        if (_owner.IsClosed) return;
        BrowserInterop.ResetComposition(_owner.Handle);
        _tracker.Reset();
    }

    /// <summary>
    /// 焦点进入可编辑元素：允许移动端弹出软键盘（inputmode="text"）并聚焦代理文本框。
    /// 桌面浏览器上文本输入始终可用，这里只影响软键盘。
    /// </summary>
    public override void Show()
    {
        if (_owner.IsClosed) return;
        _enabled = true;
        BrowserInterop.SetTextInputEnabled(_owner.Handle, true);
        BrowserInterop.Focus(_owner.Handle);
    }

    /// <summary>焦点离开可编辑元素：收起软键盘（inputmode="none"），键盘事件照常送达。</summary>
    public override void Hide()
    {
        if (_owner.IsClosed) return;
        _enabled = false;
        BrowserInterop.SetTextInputEnabled(_owner.Handle, false);
    }

    /// <summary>光标矩形（逻辑坐标，相对 canvas）：代理文本框移到这里，输入法候选窗随之定位。</summary>
    public void SetCaretRect(Rect caret)
    {
        if (_owner.IsClosed) return;
        BrowserInterop.SetTextInputRect(_owner.Handle, caret.X, caret.Y, caret.Width, caret.Height);
    }

    /// <summary>组合跟踪器的输出：先通知订阅者，再交给客户端。</summary>
    internal void OnComposition(in TextCompositionEvent e)
    {
        CompositionChanged?.Invoke(e);
        var client = Client;
        if (client is null) return;
        switch (e.Kind)
        {
            case TextCompositionKind.Update:
                client.SetComposition(e.Text, new TextInputRange(e.SelectionStart, e.SelectionEnd));
                break;
            case TextCompositionKind.End when e.Text.Length == 0:
                client.SetComposition(string.Empty, default); // 取消；有提交文本时随后经 Commit 替换预编辑
                break;
        }
    }
}

/// <summary>
/// 剪贴板（design/04：Web 上剪贴板异步且受用户手势限制，<see cref="Clipboard"/> 接口全异步）。尽力而为：
/// <list type="bullet">
/// <item><see cref="GetTextAsync"/> / <see cref="SetTextAsync"/> 走 navigator.clipboard，可能因权限、非安全上下文或缺少用户手势失败。</item>
/// <item>最可靠的路径是浏览器自己的 copy / cut / paste 事件：键盘焦点在代理文本框上时，Ctrl/⌘+C/X/V 不被阻止，浏览器派发事件。
/// 优先级：<see cref="CopyHandler"/> / <see cref="Pasted"/> 的订阅者 → 窗口的文本输入客户端（取选中文本、删除、替换选区）→
/// 粘贴的文本作为普通文本输入（OnTextInput）。</item>
/// </list>
/// 经 <see cref="PlatformBackend.Clipboard"/>、<c>platform.GetService&lt;BrowserClipboard&gt;()</c> 或 <see cref="BrowserPlatform.Clipboard"/> 取得。
/// </summary>
public sealed class BrowserClipboard : Clipboard
{
    internal BrowserClipboard() { }

    /// <summary>读取剪贴板文本；不可用、被拒绝或为空时返回 null。</summary>
    public override async Task<string?> GetTextAsync()
    {
        try
        {
            var text = await BrowserInterop.ClipboardReadText().ConfigureAwait(true);
            return string.IsNullOrEmpty(text) ? null : LimitLength(text!);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    /// <summary>写入剪贴板文本；返回是否成功。</summary>
    public override async Task<bool> SetTextAsync(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        try { return await BrowserInterop.ClipboardWriteText(LimitLength(text)).ConfigureAwait(true); }
        catch (Exception e) when (e is not OutOfMemoryException) { return false; }
    }

    /// <summary>
    /// 读取 HTML 片段（design/04 富剪贴板）：需要 <c>navigator.clipboard.read</c> 的权限与用户手势，
    /// 不可用、被拒绝、没有 HTML 格式或超时时返回 null。
    /// </summary>
    public override async Task<string?> GetHtmlAsync()
    {
        try
        {
            var html = await BrowserInterop.ClipboardReadHtml().ConfigureAwait(true);
            return string.IsNullOrEmpty(html) ? null : LimitLength(html!);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    /// <summary>写入 HTML 片段（同时生成纯文本回退）；<c>ClipboardItem</c> 不可用或被拒绝时返回 false。</summary>
    public override async Task<bool> SetHtmlAsync(string html)
    {
        if (html is null) throw new ArgumentNullException(nameof(html));
        try { return await BrowserInterop.ClipboardWriteHtml(LimitLength(html)).ConfigureAwait(true); }
        catch (Exception e) when (e is not OutOfMemoryException) { return false; }
    }

    /// <summary>读写的图片像素上限（16M 像素 ≈ 64 MB RGBA，与移动端 canvas 的面积上限一致）。</summary>
    const long MaxImagePixels = 16L * 1024 * 1024;

    /// <summary>
    /// 读取剪贴板图片：JS 把位图绘制到 canvas、取出非预乘 RGBA 像素并写入 WASM 堆中的固定缓冲区。
    /// 没有图片、权限被拒绝或尺寸超过 <see cref="MaxImagePixels"/> 时返回 null；调用方负责释放位图。
    /// </summary>
    public override async Task<Bitmap?> GetImageAsync()
    {
        try
        {
            var info = await BrowserInterop.ClipboardReadImageInfo().ConfigureAwait(true);
            if (!TryParseImageInfo(info, out int width, out int height, out int id)) return null;
            long pixels = (long)width * height;
            if (pixels <= 0 || pixels > MaxImagePixels)
            {
                BrowserInterop.ClipboardDiscardImage(id);
                return null;
            }
            int length = checked(width * height * 4);
            var rgba = new byte[length];
            var pinned = GCHandle.Alloc(rgba, GCHandleType.Pinned);
            try
            {
                if (!BrowserInterop.ClipboardTakeImage(id, Address(pinned), length)) return null;
            }
            finally
            {
                pinned.Free();
            }
            return Bitmap.FromRgbaUnpremultiplied(rgba, new PixelSize(width, height));
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return null; }
    }

    /// <summary>
    /// 写入剪贴板图片（PNG）：先把非预乘 RGBA 像素同步复制到 JS，再由 JS 编码并调用
    /// <c>navigator.clipboard.write</c>。需要用户手势与权限，失败时返回 false。
    /// </summary>
    public override async Task<bool> SetImageAsync(Bitmap image)
    {
        if (image is null) throw new ArgumentNullException(nameof(image));
        try
        {
            long pixels = (long)image.Width * image.Height;
            if (pixels <= 0 || pixels > MaxImagePixels) return false;
            byte[] rgba = image.ToRgbaUnpremultiplied();
            int id;
            var pinned = GCHandle.Alloc(rgba, GCHandleType.Pinned);
            try
            {
                id = BrowserInterop.ClipboardStageImage(image.Width, image.Height, Address(pinned), rgba.Length);
            }
            finally
            {
                pinned.Free();
            }
            if (id <= 0) return false;
            return await BrowserInterop.ClipboardWriteStagedImage(id).ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return false; }
    }

    static bool TryParseImageInfo(string? info, out int width, out int height, out int id)
    {
        width = height = id = 0;
        if (string.IsNullOrEmpty(info)) return false;
        var parts = info!.Split(',');
        return parts.Length >= 3
            && int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out width)
            && int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out height)
            && int.TryParse(parts[2], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out id)
            && id > 0;
    }

    static int Address(GCHandle pinned) => checked((int)pinned.AddrOfPinnedObject().ToInt64());

    /// <summary>
    /// 用户在窗口中粘贴（paste 事件）。有订阅者时由它处理；否则交给窗口的文本输入客户端（替换选区），
    /// 再没有时作为普通文本输入（OnTextInput）送达。
    /// </summary>
    public event Action<BrowserWindow, string>? Pasted;

    /// <summary>
    /// copy / cut 事件（参数：窗口、是否剪切）：返回要放进剪贴板的文本；返回 null 则交给文本输入客户端（若有）。
    /// 在事件中同步写入，不需要剪贴板权限。
    /// </summary>
    public Func<BrowserWindow, bool, string?>? CopyHandler { get; set; }

    internal bool RaisePasted(BrowserWindow window, string text)
    {
        var handler = Pasted;
        if (handler is null) return false;
        handler(window, text);
        return true;
    }
}
