using System;
using System.Threading.Tasks;
using AwAUI.Platform;
using AwAUI.Win32.Interop;
using static AwAUI.Win32.Interop.Const;

namespace AwAUI.Win32;

/// <summary>
/// Windows 的输入法桥（design/04「IME」，先 IMM32，TSF 以后再做）：
/// <list type="bullet">
/// <item>有客户端时窗口关联默认输入上下文（<c>ImmAssociateContextEx(IACE_DEFAULT)</c>），没有客户端或客户端是密码框时取消关联，输入法关闭。</item>
/// <item>预编辑由框架自己画：<c>WM_IME_SETCONTEXT</c> 去掉 <c>ISC_SHOWUICOMPOSITIONWINDOW</c>，<c>WM_IME_STARTCOMPOSITION</c> /
/// <c>WM_IME_COMPOSITION</c> / <c>WM_IME_ENDCOMPOSITION</c> 不交给 DefWindowProc；组合字符串（GCS_COMPSTR、GCS_CURSORPOS、
/// GCS_COMPATTR 的转换文节）→ <see cref="ITextInputClient.SetComposition"/>，结果字符串（GCS_RESULTSTR）→ <see cref="ITextInputClient.Commit"/>。
/// 候选窗仍由输入法显示。</item>
/// <item>候选窗位置：<see cref="NotifyStateChanged"/> 以客户端的插入点矩形调用 <c>ImmSetCompositionWindow(CFS_POINT)</c> 与
/// <c>ImmSetCandidateWindow(CFS_EXCLUDE)</c>（设备像素）。</item>
/// <item><see cref="ResetComposition"/>：<c>ImmNotifyIME(NI_COMPOSITIONSTR, CPS_CANCEL)</c>。</item>
/// </list>
/// 被输入法处理的按键以 <c>VK_PROCESSKEY</c> 到达窗口，不交给框架（与浏览器的 keyCode 229 一致）。
/// </summary>
public sealed unsafe class Win32TextInput : TextInputMethod
{
    readonly Win32Window _window;

    internal Win32TextInput(Win32Window window) => _window = window;

    /// <summary>当前客户端是否允许输入法组合（有客户端且不是密码框）。</summary>
    public bool UsesInputMethod => Client is { } c && !c.Options.IsPassword;

    protected override void OnClientChanged(ITextInputClient? oldClient, ITextInputClient? newClient)
    {
        var hwnd = _window.Handle;
        if (hwnd == IntPtr.Zero) return;
        if (oldClient is not null) Cancel(hwnd);
        // 0：解除关联（输入法关闭，按键直接产生 WM_CHAR）；IACE_DEFAULT：恢复窗口的默认输入上下文
        Imm32.ImmAssociateContextEx(hwnd, IntPtr.Zero, UsesInputMethod ? IACE_DEFAULT : 0);
        if (UsesInputMethod) UpdateWindows();
    }

    public override void NotifyStateChanged() => UpdateWindows();

    public override void ResetComposition()
    {
        var hwnd = _window.Handle;
        if (hwnd != IntPtr.Zero) Cancel(hwnd);
    }

    static void Cancel(IntPtr hwnd)
    {
        var himc = Imm32.ImmGetContext(hwnd);
        if (himc == IntPtr.Zero) return;
        Imm32.ImmNotifyIME(himc, NI_COMPOSITIONSTR, CPS_CANCEL, 0);
        Imm32.ImmReleaseContext(hwnd, himc);
    }

    /// <summary>把组合窗与候选窗移到客户端的插入点（逻辑像素 → 设备像素）。</summary>
    void UpdateWindows()
    {
        var hwnd = _window.Handle;
        if (hwnd == IntPtr.Zero || Client is not { } client || !UsesInputMethod) return;
        var caret = client.GetCaretRect();
        double scale = _window.RenderScale;
        int x = Px(caret.X * scale), y = Px(caret.Y * scale), bottom = Px((caret.Y + caret.Height) * scale), right = Px((caret.X + Math.Max(1, caret.Width)) * scale);
        var himc = Imm32.ImmGetContext(hwnd);
        if (himc == IntPtr.Zero) return;
        try
        {
            var cf = new COMPOSITIONFORM { dwStyle = CFS_POINT, ptCurrentPos = new POINT { X = x, Y = y }, rcArea = new RECT(x, y, right, bottom) };
            Imm32.ImmSetCompositionWindow(himc, &cf);
            var cand = new CANDIDATEFORM { dwIndex = 0, dwStyle = CFS_EXCLUDE, ptCurrentPos = new POINT { X = x, Y = bottom }, rcArea = new RECT(x, y, right, bottom) };
            Imm32.ImmSetCandidateWindow(himc, &cand);
        }
        finally
        {
            Imm32.ImmReleaseContext(hwnd, himc);
        }
    }

    static int Px(double v) => double.IsNaN(v) ? 0 : (int)Math.Round(Math.Max(-1e6, Math.Min(1e6, v)));

    /// <summary>窗口过程中的输入法消息。返回 true 表示已处理（<paramref name="result"/> 为返回值），否则交给 DefWindowProc。</summary>
    internal bool HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, ref IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        if (!UsesInputMethod) return false;
        switch (msg)
        {
            case WM_IME_SETCONTEXT:
                // 预编辑由框架画：不让输入法显示自己的组合窗（候选窗照常）；修改后的 lParam 交给 DefWindowProc
                if (wParam != IntPtr.Zero) lParam = (IntPtr)ImeComposition.WithoutCompositionWindow((long)lParam);
                return false;
            case WM_IME_STARTCOMPOSITION:
                UpdateWindows();
                return true;
            case WM_IME_COMPOSITION:
                OnComposition(hwnd, (uint)(long)lParam);
                return true;
            case WM_IME_ENDCOMPOSITION:
                // 结束而没有结果字符串（取消，或结果已经在 WM_IME_COMPOSITION 中提交）：清除预编辑
                if (Client is { Composition: not null } c) c.SetComposition(string.Empty, default);
                return true;
        }
        return false;
    }

    void OnComposition(IntPtr hwnd, uint flags)
    {
        var himc = Imm32.ImmGetContext(hwnd);
        if (himc == IntPtr.Zero) return;
        try
        {
            if ((flags & GCS_RESULTSTR) != 0 && Client is { } c1)
            {
                var result = ReadString(himc, GCS_RESULTSTR, Clipboard.MaxTextLength);
                if (result.Length > 0) c1.Commit(result);
                else if (c1.Composition is not null) c1.SetComposition(string.Empty, default);
            }
            if ((flags & GCS_COMPSTR) != 0 && Client is { } c2 && UsesInputMethod)
            {
                var comp = ReadString(himc, GCS_COMPSTR, ImeComposition.MaxLength);
                int cursor = (flags & GCS_CURSORPOS) != 0 ? Imm32.ImmGetCompositionStringW(himc, GCS_CURSORPOS, null, 0) : -1;
                byte[]? attrs = null;
                int count = 0;
                if ((flags & GCS_COMPATTR) != 0)
                {
                    int bytes = Imm32.ImmGetCompositionStringW(himc, GCS_COMPATTR, null, 0);
                    if (bytes > 0)
                    {
                        attrs = new byte[Math.Min(bytes, ImeComposition.MaxLength)];
                        fixed (byte* p = attrs) count = Imm32.ImmGetCompositionStringW(himc, GCS_COMPATTR, p, (uint)attrs.Length);
                    }
                }
                c2.SetComposition(comp, ImeComposition.Selection(attrs, count, cursor, comp.Length));
            }
        }
        finally
        {
            Imm32.ImmReleaseContext(hwnd, himc);
        }
    }

    static string ReadString(IntPtr himc, uint index, int maxChars)
    {
        int bytes = Imm32.ImmGetCompositionStringW(himc, index, null, 0);
        int chars = Math.Min(ImeComposition.CharCount(bytes), maxChars);
        if (chars == 0) return string.Empty;
        var buffer = new char[chars];
        int got;
        fixed (char* p = buffer) got = Imm32.ImmGetCompositionStringW(himc, index, p, (uint)(chars * 2));
        int n = Math.Min(ImeComposition.CharCount(got), chars);
        if (n > 0 && char.IsHighSurrogate(buffer[n - 1]) && n == chars && bytes / 2 > chars) n--;
        return new string(buffer, 0, n);
    }
}

/// <summary>
/// Windows 剪贴板（CF_UNICODETEXT）。读写同步完成（返回已完成的任务）；剪贴板被其他程序占用时短暂重试，仍失败则返回 null / false。
/// 写入时 LF 转为 CRLF，读取时规范回 LF；读取长度以 GlobalSize 与 <see cref="Clipboard.MaxTextLength"/> 为界。
/// </summary>
public sealed unsafe class Win32Clipboard : Clipboard
{
    readonly Win32Platform _platform;

    internal Win32Clipboard(Win32Platform platform) => _platform = platform;

    public override Task<string?> GetTextAsync() => OnUiThread(Read);

    public override Task<bool> SetTextAsync(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        return OnUiThread(() => Write(text));
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

    bool Open()
    {
        // 其他程序打开剪贴板期间 OpenClipboard 失败：最多重试 10 次（约 20 ms），不长时间阻塞 UI 线程
        for (int i = 0; i < 10; i++)
        {
            if (User32.OpenClipboard(_platform.MessageWindow) != 0) return true;
            Kernel32.Sleep(2);
        }
        return false;
    }

    string? Read()
    {
        if (User32.IsClipboardFormatAvailable(CF_UNICODETEXT) == 0 || !Open()) return null;
        try
        {
            var handle = User32.GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero) return null;
            var p = (char*)Kernel32.GlobalLock(handle);
            if (p == null) return null;
            try
            {
                long capacity = (long)(ulong)Kernel32.GlobalSize(handle) / 2;
                var text = Win32ClipboardText.Decode(p, capacity);
                return text.Length == 0 ? null : text;
            }
            finally
            {
                Kernel32.GlobalUnlock(handle);
            }
        }
        finally
        {
            User32.CloseClipboard();
        }
    }

    bool Write(string text)
    {
        string data = Win32ClipboardText.ToCrLf(LimitLength(text));
        if (!Open()) return false;
        try
        {
            if (User32.EmptyClipboard() == 0) return false;
            if (data.Length == 0) return true;
            var bytes = (ulong)(data.Length + 1) * 2;
            var handle = Kernel32.GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            if (handle == IntPtr.Zero) return false;
            var p = (char*)Kernel32.GlobalLock(handle);
            if (p == null)
            {
                Kernel32.GlobalFree(handle);
                return false;
            }
            fixed (char* src = data) Buffer.MemoryCopy(src, p, (long)bytes, data.Length * 2L);
            p[data.Length] = '\0';
            Kernel32.GlobalUnlock(handle);
            // 成功后内存归系统所有；失败时由我们释放
            if (User32.SetClipboardData(CF_UNICODETEXT, handle) == IntPtr.Zero)
            {
                Kernel32.GlobalFree(handle);
                return false;
            }
            return true;
        }
        finally
        {
            User32.CloseClipboard();
        }
    }
}
