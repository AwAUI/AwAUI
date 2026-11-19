using System;
using System.Runtime.InteropServices;

namespace AwAUI.X11.Interop;

/// <summary>
/// 原生 → 托管的回调入口（与 AwAUI.MacOS 相同的双轨模式）：现代 .NET 用 [UnmanagedCallersOnly] + 函数指针，
/// netstandard2.0 基线用保存在静态字段中的委托 + Marshal.GetFunctionPointerForDelegate。
/// 回调中的异常绝不穿过原生栈帧，统一在这里捕获。
/// </summary>
internal static unsafe class Callbacks
{
    /// <summary>int (*XErrorHandler)(Display*, XErrorEvent*)。在调用 Xlib 的线程上（即主线程）同步触发。</summary>
    public static Action<IntPtr, IntPtr>? OnError;

    static void Report(Exception e)
    {
        try { Console.Error.WriteLine("[AwAUI.X11] Unhandled exception in native callback: " + e); } catch { }
    }

    static int ErrorImpl(IntPtr display, IntPtr errorEvent)
    {
        try { OnError?.Invoke(display, errorEvent); } catch (Exception e) { Report(e); }
        return 0; // 返回值被 Xlib 忽略
    }

    /// <summary>
    /// XIM 预编辑回调 (XIC, client_data, call_data)：kind 为 0 start、1 done、2 draw、3 caret。在 XFilterEvent / Xutf8LookupString 内部同步调用。
    /// </summary>
    public static Action<int, IntPtr, IntPtr>? OnPreedit;

    static int PreeditImpl(int kind, IntPtr clientData, IntPtr callData)
    {
        try { OnPreedit?.Invoke(kind, clientData, callData); } catch (Exception e) { Report(e); }
        return -1; // PreeditStart：预编辑长度不限
    }

#if NET8_0_OR_GREATER
    [UnmanagedCallersOnly] static int ErrorEntry(IntPtr display, IntPtr errorEvent) => ErrorImpl(display, errorEvent);
    [UnmanagedCallersOnly] static int PreeditStartEntry(IntPtr ic, IntPtr client, IntPtr call) => PreeditImpl(0, client, call);
    [UnmanagedCallersOnly] static void PreeditDoneEntry(IntPtr ic, IntPtr client, IntPtr call) => PreeditImpl(1, client, call);
    [UnmanagedCallersOnly] static void PreeditDrawEntry(IntPtr ic, IntPtr client, IntPtr call) => PreeditImpl(2, client, call);
    [UnmanagedCallersOnly] static void PreeditCaretEntry(IntPtr ic, IntPtr client, IntPtr call) => PreeditImpl(3, client, call);

    public static IntPtr Error => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, int>)&ErrorEntry;
    public static IntPtr PreeditStart => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)&PreeditStartEntry;
    public static IntPtr PreeditDone => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&PreeditDoneEntry;
    public static IntPtr PreeditDraw => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&PreeditDrawEntry;
    public static IntPtr PreeditCaret => (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&PreeditCaretEntry;
#else
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int PreeditStartFn(IntPtr ic, IntPtr client, IntPtr call);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void PreeditFn(IntPtr ic, IntPtr client, IntPtr call);

    static readonly PreeditStartFn s_preeditStart = (ic, c, d) => PreeditImpl(0, c, d);
    static readonly PreeditFn s_preeditDone = (ic, c, d) => PreeditImpl(1, c, d);
    static readonly PreeditFn s_preeditDraw = (ic, c, d) => PreeditImpl(2, c, d);
    static readonly PreeditFn s_preeditCaret = (ic, c, d) => PreeditImpl(3, c, d);

    public static IntPtr PreeditStart => Marshal.GetFunctionPointerForDelegate(s_preeditStart);
    public static IntPtr PreeditDone => Marshal.GetFunctionPointerForDelegate(s_preeditDone);
    public static IntPtr PreeditDraw => Marshal.GetFunctionPointerForDelegate(s_preeditDraw);
    public static IntPtr PreeditCaret => Marshal.GetFunctionPointerForDelegate(s_preeditCaret);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int ErrorFn(IntPtr display, IntPtr errorEvent);

    static readonly ErrorFn s_error = ErrorImpl;

    public static IntPtr Error => Marshal.GetFunctionPointerForDelegate(s_error);
#endif
}

/// <summary>
/// X 协议错误的集中处理。Xlib 默认的错误处理器会打印并 exit() 进程；我们换成只记录的处理器，
/// 并提供「捕获」区段供可预期失败的请求（例如远程显示上的 XShmAttach）探测错误而不终止进程。
/// </summary>
internal static unsafe class XErrors
{
    static bool s_installed;
    static int s_trapDepth;
    static int s_trapped;

    public static void Install()
    {
        if (s_installed) return;
        s_installed = true;
        Callbacks.OnError = Handle;
        Xlib.XSetErrorHandler(Callbacks.Error);
    }

    /// <summary>开始捕获：之后的错误只记录不打印。调用方随后 XSync 并以 <see cref="Untrap"/> 取回错误码。</summary>
    public static void Trap()
    {
        s_trapDepth++;
        s_trapped = 0;
    }

    /// <summary>结束捕获，返回期间第一个错误码（0 表示没有错误）。调用前应先 XSync。</summary>
    public static int Untrap()
    {
        if (s_trapDepth > 0) s_trapDepth--;
        int code = s_trapped;
        s_trapped = 0;
        return code;
    }

    static void Handle(IntPtr display, IntPtr errorEvent)
    {
        var e = (XErrorEvent*)errorEvent;
        if (s_trapDepth > 0)
        {
            if (s_trapped == 0) s_trapped = e->error_code == 0 ? -1 : e->error_code;
            return;
        }
        var buffer = stackalloc byte[256];
        buffer[0] = 0;
        Xlib.XGetErrorText(display, e->error_code, buffer, 256);
        int len = 0;
        while (len < 255 && buffer[len] != 0) len++;
        var text = new string((sbyte*)buffer, 0, len);
        Console.Error.WriteLine($"[AwAUI.X11] X error: {text} (code {e->error_code}, request {e->request_code}.{e->minor_code}, resource 0x{e->resourceid:x})");
    }
}
