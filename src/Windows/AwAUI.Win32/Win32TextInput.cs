using System;
using System.Threading.Tasks;
using AwAUI.Platform;
using AwAUI.Win32.Interop;
using static AwAUI.Win32.Interop.Const;

namespace AwAUI.Win32;

/// <summary>
/// Windows 的输入法桥（design/04「IME」，先 IMM32，TSF 以后再做）：
/// <list type="bullet">
/// <item>有客户端时创建并关联输入上下文（<c>ImmCreateContext</c> + <c>ImmAssociateContext</c>；失败时退回
/// <c>ImmAssociateContextEx(IACE_DEFAULT)</c>），没有客户端时解除关联；窗口销毁时销毁自建的上下文。密码框也保留关联，
/// 预编辑的遮罩由 UI 层负责。</item>
/// <item>预编辑由框架自己画：<c>WM_IME_SETCONTEXT</c> 去掉 <c>ISC_SHOWUICOMPOSITIONWINDOW</c>，<c>WM_IME_STARTCOMPOSITION</c> /
/// <c>WM_IME_COMPOSITION</c> / <c>WM_IME_ENDCOMPOSITION</c> 不交给 DefWindowProc；组合字符串（GCS_COMPSTR、GCS_CURSORPOS、
/// GCS_COMPATTR 的转换文节）→ <see cref="ITextInputClient.SetComposition"/>，结果字符串（GCS_RESULTSTR）→ <see cref="ITextInputClient.Commit"/>。
/// 候选窗仍由输入法显示。</item>
/// <item>结果串只在 GCS_RESULTSTR 提交一次；IMM32 随后重复投递的 WM_CHAR 与组合期间的 WM_CHAR 由窗口抑制（见
/// <see cref="Win32Window.IgnoreNextChar"/>）。<c>WM_IME_ENDCOMPOSITION</c> 没有结果串时把当前组合提交为文本。</item>
/// <item>候选窗位置：<see cref="NotifyStateChanged"/> 以客户端的插入点矩形调用 <c>ImmSetCompositionWindow(CFS_POINT)</c> 与
/// <c>ImmSetCandidateWindow(CFS_EXCLUDE)</c>（设备像素）。</item>
/// <item><see cref="ResetComposition"/>：<c>ImmNotifyIME(NI_COMPOSITIONSTR, CPS_CANCEL)</c>。</item>
/// </list>
/// 被输入法处理的按键以 <c>VK_PROCESSKEY</c> 到达窗口，不交给框架（与浏览器的 keyCode 229 一致）。
/// </summary>
public sealed unsafe class Win32TextInput : TextInputMethod
{
    readonly Win32Window _window;

    /// <summary>自建的输入上下文；<see cref="IntPtr.Zero"/> 表示尚未创建或已销毁。</summary>
    IntPtr _himc;
    /// <summary>当前窗口是否关联着输入上下文（自建的，或后备路径由系统创建的）。</summary>
    bool _associated;

    internal Win32TextInput(Win32Window window) => _window = window;

    /// <summary>当前是否有文本输入客户端。密码框同样使用输入法（预编辑的遮罩由 UI 层负责）。</summary>
    public bool UsesInputMethod => Client is not null;

    protected override void OnClientChanged(ITextInputClient? oldClient, ITextInputClient? newClient)
    {
        var hwnd = _window.Handle;
        if (hwnd == IntPtr.Zero) return;
        if (oldClient is not null) Cancel(hwnd);
        if (newClient is not null)
        {
            Associate(hwnd);
            UpdateWindows();
        }
        else
        {
            Disassociate(hwnd);
        }
    }

    /// <summary>
    /// 让窗口关联输入上下文。优先显式创建（<c>ImmCreateContext</c>）再关联（<c>ImmAssociateContext</c>）；从未有过上下文的窗口
    /// 不会由 <c>ImmAssociateContextEx(IACE_DEFAULT)</c> 自动创建，所以后者只作失败时的后备。
    /// </summary>
    void Associate(IntPtr hwnd)
    {
        if (_himc == IntPtr.Zero) _himc = Imm32.ImmCreateContext();
        if (_himc != IntPtr.Zero)
        {
            Imm32.ImmAssociateContext(hwnd, _himc);
            // ImmAssociateContext 的返回值是之前关联的上下文（可能为 0），不能据此判断成功：用 ImmGetContext 验证
            if (HasContext(hwnd))
            {
                _associated = true;
                return;
            }
        }
        _associated = false;
        // 后备：让系统为窗口创建并关联默认输入上下文
        if (Imm32.ImmAssociateContextEx(hwnd, IntPtr.Zero, IACE_DEFAULT) != 0 || HasContext(hwnd))
            _associated = true;
    }

    /// <summary>解除关联但保留自建的上下文，下一次有客户端时直接复用。</summary>
    void Disassociate(IntPtr hwnd)
    {
        if (!_associated) return;
        Imm32.ImmAssociateContext(hwnd, IntPtr.Zero);
        _associated = false;
    }

    /// <summary>窗口销毁前解除关联并销毁自建的输入上下文。</summary>
    internal void Release(IntPtr hwnd)
    {
        if (_associated && hwnd != IntPtr.Zero) Imm32.ImmAssociateContext(hwnd, IntPtr.Zero);
        _associated = false;
        if (_himc != IntPtr.Zero)
        {
            Imm32.ImmDestroyContext(_himc);
            _himc = IntPtr.Zero;
        }
    }

    static bool HasContext(IntPtr hwnd)
    {
        var himc = Imm32.ImmGetContext(hwnd);
        if (himc == IntPtr.Zero) return false;
        Imm32.ImmReleaseContext(hwnd, himc);
        return true;
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
                // 没有结果串（取消，或某些输入法只在结束时提交）：当前组合非空时作为文本提交，再清空
                HandleCompositionEnd();
                return true;
        }
        return false;
    }

    void HandleCompositionEnd()
    {
        if (Client is { Composition: { } composition } client)
        {
            var text = client.GetText(composition);
            if (text.Length > 0) client.Commit(text);
            else client.SetComposition(string.Empty, default);
        }
    }

    /// <summary>
    /// 读取 WM_IME_COMPOSITION 的字符串。先在持有 HIMC 期间把结果串、组合串与选区读入局部变量，释放上下文之后再回调客户端：
    /// 客户端可能在回调中重入消息循环、切换客户端或销毁窗口。
    /// </summary>
    void OnComposition(IntPtr hwnd, uint flags)
    {
        string? result = null;
        string? composition = null;
        TextInputRange compositionSelection = default;
        var himc = Imm32.ImmGetContext(hwnd);
        if (himc == IntPtr.Zero) return;
        try
        {
            if ((flags & GCS_RESULTSTR) != 0)
            {
                result = ReadString(himc, GCS_RESULTSTR, Clipboard.MaxTextLength);
                // 在回调之前置位：客户端重入消息循环时，IMM32 重复投递的 WM_CHAR 同样会被抑制
                _window.IgnoreNextChar();
            }
            if ((flags & GCS_COMPSTR) != 0)
            {
                composition = ReadString(himc, GCS_COMPSTR, ImeComposition.MaxLength);
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
                compositionSelection = ImeComposition.Selection(attrs, count, cursor, composition.Length);
            }
        }
        finally
        {
            Imm32.ImmReleaseContext(hwnd, himc);
        }

        // 上下文已释放：下面可能重入消息循环
        if (result is not null && Client is { } resultClient)
        {
            if (result.Length > 0) resultClient.Commit(result);
            else if (resultClient.Composition is not null) resultClient.SetComposition(string.Empty, default);
        }
        if (composition is not null && Client is { } compositionClient)
            compositionClient.SetComposition(composition, compositionSelection);
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
/// Windows 剪贴板。文本基线为 CF_UNICODETEXT；HTML（"HTML Format"）、CF_DIB 图片与 CF_HDROP 文件列表在
/// <c>Win32Clipboard.Rich.cs</c> 的同一 partial 类里实现。读写同步完成（返回已完成的任务）；剪贴板被其他程序占用时
/// 短暂重试，仍失败则返回 null / false。写入时 LF 转为 CRLF，读取时规范回 LF；读取长度以 GlobalSize 与
/// <see cref="Clipboard.MaxTextLength"/> 为界。
/// </summary>
public sealed unsafe partial class Win32Clipboard : Clipboard
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
