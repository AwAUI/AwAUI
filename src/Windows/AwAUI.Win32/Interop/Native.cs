using System;
using System.Runtime.InteropServices;

namespace AwAUI.Win32.Interop;

/// <summary>
/// user32 的裸互操作。所有签名只用 blittable 类型（design/11）：字符串用 char*（调用方以 fixed 固定，
/// .NET 字符串固定后以 '\0' 结尾），BOOL 用 int，句柄用 IntPtr。显式使用 W 版本并设置 ExactSpelling，
/// 避免运行时按 CharSet 探测 A/W 后缀。32 位上 DllImport 默认调用约定为 Winapi（即 stdcall）。
/// </summary>
internal static unsafe class User32
{
    const string Lib = "user32.dll";

    // ------------------------------------------------------------ 窗口类与窗口
    [DllImport(Lib, ExactSpelling = true, SetLastError = true)] public static extern ushort RegisterClassExW(WNDCLASSEXW* wc);
    [DllImport(Lib, ExactSpelling = true, SetLastError = true)]
    public static extern IntPtr CreateWindowExW(uint exStyle, char* className, char* windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport(Lib, ExactSpelling = true)] public static extern int DestroyWindow(IntPtr hwnd);
    [DllImport(Lib, ExactSpelling = true)] public static extern int ShowWindow(IntPtr hwnd, int cmdShow);
    [DllImport(Lib, ExactSpelling = true)] public static extern int SetWindowTextW(IntPtr hwnd, char* text);
    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport(Lib, ExactSpelling = true)] public static extern int GetClientRect(IntPtr hwnd, RECT* rect);
    [DllImport(Lib, ExactSpelling = true)] public static extern int ScreenToClient(IntPtr hwnd, POINT* point);
    [DllImport(Lib, ExactSpelling = true)]
    public static extern int SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport(Lib, ExactSpelling = true)] public static extern int AdjustWindowRectEx(RECT* rect, uint style, int menu, uint exStyle);
    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport(Lib, ExactSpelling = true)] public static extern int GetMonitorInfoW(IntPtr monitor, MONITORINFO* info);

    // ------------------------------------------------------------ 消息循环
    [DllImport(Lib, ExactSpelling = true)] public static extern int GetMessageW(MSG* msg, IntPtr hwnd, uint filterMin, uint filterMax);
    [DllImport(Lib, ExactSpelling = true)] public static extern int TranslateMessage(MSG* msg);
    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr DispatchMessageW(MSG* msg);
    [DllImport(Lib, ExactSpelling = true)] public static extern int PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport(Lib, ExactSpelling = true)] public static extern void PostQuitMessage(int exitCode);
    [DllImport(Lib, ExactSpelling = true)] public static extern int GetMessageTime();
    [DllImport(Lib, ExactSpelling = true)] public static extern int SystemParametersInfoW(uint action, uint param, void* pvParam, uint winIni);

    // ------------------------------------------------------------ 绘制
    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr BeginPaint(IntPtr hwnd, PAINTSTRUCT* ps);
    [DllImport(Lib, ExactSpelling = true)] public static extern int EndPaint(IntPtr hwnd, PAINTSTRUCT* ps);
    [DllImport(Lib, ExactSpelling = true)] public static extern int InvalidateRect(IntPtr hwnd, RECT* rect, int erase);
    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport(Lib, ExactSpelling = true)] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport(Lib, ExactSpelling = true)] public static extern int FillRect(IntPtr hdc, RECT* rect, IntPtr brush);

    // ------------------------------------------------------------ 输入
    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport(Lib, ExactSpelling = true)] public static extern int ReleaseCapture();
    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr GetCapture();
    [DllImport(Lib, ExactSpelling = true)] public static extern int TrackMouseEvent(TRACKMOUSEEVENT* tme);
    [DllImport(Lib, ExactSpelling = true)] public static extern short GetKeyState(int virtualKey);
    [DllImport(Lib, ExactSpelling = true)] public static extern uint MapVirtualKeyW(uint code, uint mapType);
    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr LoadCursorW(IntPtr instance, IntPtr cursorName);
    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport(Lib, ExactSpelling = true)] public static extern int GetKeyboardState(byte* state);
    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr GetKeyboardLayout(uint threadId);
    /// <summary>flags 第 2 位（4）：不改变内核的键盘状态（不消耗死键），Windows 10 1607+。</summary>
    [DllImport(Lib, ExactSpelling = true)]
    public static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte* keyState, char* buffer, int bufferChars, uint flags, IntPtr layout);

    // ------------------------------------------------------------ 剪贴板
    [DllImport(Lib, ExactSpelling = true, SetLastError = true)] public static extern int OpenClipboard(IntPtr owner);
    [DllImport(Lib, ExactSpelling = true)] public static extern int CloseClipboard();
    [DllImport(Lib, ExactSpelling = true)] public static extern int EmptyClipboard();
    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr GetClipboardData(uint format);
    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport(Lib, ExactSpelling = true)] public static extern int IsClipboardFormatAvailable(uint format);

    // ------------------------------------------------------------ DPI（较新系统才有的入口点：调用方须捕获 EntryPointNotFoundException）
    /// <summary>Windows 10 1607+（DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 需 1703+）。</summary>
    [DllImport(Lib, ExactSpelling = true)] public static extern int SetProcessDpiAwarenessContext(IntPtr context);
    /// <summary>Windows Vista+。</summary>
    [DllImport(Lib, ExactSpelling = true)] public static extern int SetProcessDPIAware();
    /// <summary>Windows 10 1607+。</summary>
    [DllImport(Lib, ExactSpelling = true)] public static extern uint GetDpiForWindow(IntPtr hwnd);
    /// <summary>Windows 10 1607+。</summary>
    [DllImport(Lib, ExactSpelling = true)]
    public static extern int AdjustWindowRectExForDpi(RECT* rect, uint style, int menu, uint exStyle, uint dpi);
}

internal static unsafe class Gdi32
{
    const string Lib = "gdi32.dll";

    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport(Lib, ExactSpelling = true)] public static extern int DeleteDC(IntPtr hdc);
    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport(Lib, ExactSpelling = true)] public static extern int DeleteObject(IntPtr obj);
    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr GetStockObject(int index);
    [DllImport(Lib, ExactSpelling = true)]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, BITMAPINFO* info, uint usage, void** bits, IntPtr section, uint offset);
    [DllImport(Lib, ExactSpelling = true)]
    public static extern int BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);
    [DllImport(Lib, ExactSpelling = true)] public static extern int GdiFlush();
    [DllImport(Lib, ExactSpelling = true)] public static extern int GetDeviceCaps(IntPtr hdc, int index);
}

internal static unsafe class Kernel32
{
    [DllImport("kernel32.dll", ExactSpelling = true)] public static extern IntPtr GetModuleHandleW(char* moduleName);
    [DllImport("kernel32.dll", ExactSpelling = true)] public static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", ExactSpelling = true)] public static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("kernel32.dll", ExactSpelling = true)] public static extern void* GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll", ExactSpelling = true)] public static extern int GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll", ExactSpelling = true)] public static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("kernel32.dll", ExactSpelling = true)] public static extern void Sleep(uint milliseconds);
}

/// <summary>imm32.dll：输入法（IMM32）。所有 Windows 版本都有。</summary>
internal static unsafe class Imm32
{
    const string Lib = "imm32.dll";

    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr ImmGetContext(IntPtr hwnd);
    [DllImport(Lib, ExactSpelling = true)] public static extern int ImmReleaseContext(IntPtr hwnd, IntPtr himc);
    /// <summary>返回字节数（字符串）或值（GCS_CURSORPOS）；负数为错误。</summary>
    [DllImport(Lib, ExactSpelling = true)] public static extern int ImmGetCompositionStringW(IntPtr himc, uint index, void* buffer, uint bufferBytes);
    [DllImport(Lib, ExactSpelling = true)] public static extern int ImmSetCompositionWindow(IntPtr himc, COMPOSITIONFORM* form);
    [DllImport(Lib, ExactSpelling = true)] public static extern int ImmSetCandidateWindow(IntPtr himc, CANDIDATEFORM* form);
    [DllImport(Lib, ExactSpelling = true)] public static extern int ImmNotifyIME(IntPtr himc, uint action, uint index, uint value);
    [DllImport(Lib, ExactSpelling = true)] public static extern int ImmAssociateContextEx(IntPtr hwnd, IntPtr himc, uint flags);
}

/// <summary>shcore.dll：Windows 8.1+。旧系统上调用会抛出 DllNotFoundException / EntryPointNotFoundException。</summary>
internal static class Shcore
{
    [DllImport("shcore.dll", ExactSpelling = true)] public static extern int SetProcessDpiAwareness(int value);
}

/// <summary>dwmapi.dll：Windows Vista+。</summary>
internal static class Dwmapi
{
    [DllImport("dwmapi.dll", ExactSpelling = true)] public static extern int DwmFlush();
}

/// <summary>Windows SDK 常量（winuser.h / wingdi.h / shellscalingapi.h）。</summary>
internal static class Const
{
    // 窗口样式
    public const uint WS_OVERLAPPED = 0x00000000;
    public const uint WS_CAPTION = 0x00C00000;
    public const uint WS_SYSMENU = 0x00080000;
    public const uint WS_THICKFRAME = 0x00040000;
    public const uint WS_MINIMIZEBOX = 0x00020000;
    public const uint WS_MAXIMIZEBOX = 0x00010000;
    public const uint WS_OVERLAPPEDWINDOW = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX; // 0x00CF0000

    public const uint CS_VREDRAW = 0x0001;
    public const uint CS_HREDRAW = 0x0002;

    public const int CW_USEDEFAULT = unchecked((int)0x80000000);
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    public const int SW_SHOWNORMAL = 1;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;

    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    // 消息
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_SIZE = 0x0005;
    public const uint WM_SETFOCUS = 0x0007;
    public const uint WM_KILLFOCUS = 0x0008;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_SETCURSOR = 0x0020;
    public const uint WM_GETMINMAXINFO = 0x0024;
    public const uint WM_NCDESTROY = 0x0082;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_CHAR = 0x0102;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_SYSKEYUP = 0x0105;
    public const uint WM_SYSCHAR = 0x0106;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_MBUTTONDOWN = 0x0207;
    public const uint WM_MBUTTONUP = 0x0208;
    public const uint WM_MOUSEWHEEL = 0x020A;
    public const uint WM_XBUTTONDOWN = 0x020B;
    public const uint WM_XBUTTONUP = 0x020C;
    public const uint WM_MOUSEHWHEEL = 0x020E;
    public const uint WM_CAPTURECHANGED = 0x0215;
    public const uint WM_MOUSELEAVE = 0x02A3;
    public const uint WM_DPICHANGED = 0x02E0;
    public const uint WM_APP = 0x8000;

    public const int SIZE_MINIMIZED = 1;
    public const int HTCLIENT = 1;
    public const int WHEEL_DELTA = 120;
    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint SPI_GETWHEELSCROLLLINES = 0x0068, SPI_SETWHEELSCROLLLINES = 0x0069;
    public const uint SPI_GETWHEELSCROLLCHARS = 0x006C, SPI_SETWHEELSCROLLCHARS = 0x006D;
    public const int XBUTTON1 = 0x0001;
    public const int XBUTTON2 = 0x0002;

    // wParam 中的 MK_* 按键状态
    public const int MK_LBUTTON = 0x0001;
    public const int MK_RBUTTON = 0x0002;
    public const int MK_MBUTTON = 0x0010;
    public const int MK_XBUTTON1 = 0x0020;
    public const int MK_XBUTTON2 = 0x0040;

    public const uint TME_LEAVE = 0x00000002;
    public const uint TME_CANCEL = 0x80000000;

    // 虚拟键
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;

    public const uint MAPVK_VK_TO_VSC_EX = 4;
    public const int VK_LCONTROL = 0xA2;
    public const int VK_RCONTROL = 0xA3;
    public const int VK_LMENU = 0xA4;
    public const int VK_RMENU = 0xA5;
    public const int VK_PROCESSKEY = 0xE5;

    // 输入法（imm.h）
    public const uint WM_IME_STARTCOMPOSITION = 0x010D;
    public const uint WM_IME_ENDCOMPOSITION = 0x010E;
    public const uint WM_IME_COMPOSITION = 0x010F;
    public const uint WM_IME_SETCONTEXT = 0x0281;
    public const uint GCS_COMPSTR = 0x0008;
    public const uint GCS_COMPATTR = 0x0010;
    public const uint GCS_CURSORPOS = 0x0080;
    public const uint GCS_RESULTSTR = 0x0800;
    public const uint ISC_SHOWUICOMPOSITIONWINDOW = 0x80000000;
    public const uint CFS_POINT = 0x0002;
    public const uint CFS_EXCLUDE = 0x0080;
    public const uint NI_COMPOSITIONSTR = 0x0015;
    public const uint CPS_COMPLETE = 0x0001;
    public const uint CPS_CANCEL = 0x0004;
    public const uint IACE_DEFAULT = 0x0010;

    // 剪贴板
    public const uint CF_UNICODETEXT = 13;
    public const uint GMEM_MOVEABLE = 0x0002;

    // 系统光标（MAKEINTRESOURCE）
    public const int IDC_ARROW = 32512;
    public const int IDC_IBEAM = 32513;
    public const int IDC_WAIT = 32514;
    public const int IDC_CROSS = 32515;
    public const int IDC_SIZEWE = 32644;
    public const int IDC_SIZENS = 32645;
    public const int IDC_NO = 32648;
    public const int IDC_HAND = 32649;

    // GDI
    public const uint BI_RGB = 0;
    public const uint DIB_RGB_COLORS = 0;
    public const uint SRCCOPY = 0x00CC0020;
    public const int LOGPIXELSX = 88;
    public const int BLACK_BRUSH = 4;

    // DPI
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);
    public const int PROCESS_PER_MONITOR_DPI_AWARE = 2;
    public const uint USER_DEFAULT_SCREEN_DPI = 96;
}
