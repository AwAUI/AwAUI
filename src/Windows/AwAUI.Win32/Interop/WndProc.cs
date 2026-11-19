using System;
using System.Runtime.InteropServices;

namespace AwAUI.Win32.Interop;

/// <summary>
/// 窗口过程入口（原生 → 托管）。现代 .NET 使用 [UnmanagedCallersOnly] + stdcall 函数指针（无封送、NativeAOT 友好）；
/// netstandard2.0 基线使用 stdcall 委托 + Marshal.GetFunctionPointerForDelegate（委托保存在静态字段中以免被回收）。
/// stdcall 只在 x86 上有意义，x64 / ARM64 上忽略。回调中的异常绝不能穿过原生栈帧，统一在这里捕获。
/// </summary>
internal static unsafe class WndProc
{
    static void Report(Exception e)
    {
        try { Console.Error.WriteLine("[AwAUI.Win32] Unhandled exception in window procedure: " + e); } catch { }
    }

    static IntPtr Impl(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try { return Win32Platform.HandleMessage(hwnd, msg, wParam, lParam); }
        catch (Exception e) { Report(e); return IntPtr.Zero; }
    }

#if NET8_0_OR_GREATER
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    static IntPtr Entry(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam) => Impl(hwnd, msg, wParam, lParam);

    public static IntPtr Pointer => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&Entry;
#else
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate IntPtr WndProcFn(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    static readonly WndProcFn s_wndProc = Impl;

    public static IntPtr Pointer => Marshal.GetFunctionPointerForDelegate(s_wndProc);
#endif
}
