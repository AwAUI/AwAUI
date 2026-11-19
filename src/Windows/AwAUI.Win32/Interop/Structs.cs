using System;
using System.Runtime.InteropServices;

namespace AwAUI.Win32.Interop;

// Win32 结构体。全部为 blittable：句柄、WPARAM、LPARAM、指针一律用 IntPtr（随进程位数变化），
// 其余字段与 Windows SDK 头文件逐一对应；Sequential 布局按 C 的自然对齐填充，32 位与 64 位均正确。

/// <summary>POINT（windef.h）：8 字节。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X, Y;
}

/// <summary>RECT（windef.h）：16 字节。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left, Top, Right, Bottom;
    public RECT(int left, int top, int right, int bottom) { Left = left; Top = top; Right = right; Bottom = bottom; }
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>MSG（winuser.h）：x64 为 48 字节，x86 为 32 字节（含 lPrivate；较新 SDK 无条件声明该字段，多出的空间无害）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public IntPtr hwnd;
    public uint message;
    public IntPtr wParam;
    public IntPtr lParam;
    public uint time;
    public POINT pt;
    public uint lPrivate;
}

/// <summary>WNDCLASSEXW（winuser.h）：x64 为 80 字节，x86 为 48 字节。</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WNDCLASSEXW
{
    public uint cbSize;
    public uint style;
    public IntPtr lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public IntPtr hInstance;
    public IntPtr hIcon;
    public IntPtr hCursor;
    public IntPtr hbrBackground;
    public char* lpszMenuName;
    public char* lpszClassName;
    public IntPtr hIconSm;
}

/// <summary>PAINTSTRUCT（winuser.h）：x64 为 72 字节，x86 为 64 字节。</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PAINTSTRUCT
{
    public IntPtr hdc;
    public int fErase;
    public RECT rcPaint;
    public int fRestore;
    public int fIncUpdate;
    public fixed byte rgbReserved[32];
}

/// <summary>BITMAPINFOHEADER（wingdi.h）：40 字节，与位数无关。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth;
    public int biHeight;
    public ushort biPlanes;
    public ushort biBitCount;
    public uint biCompression;
    public uint biSizeImage;
    public int biXPelsPerMeter;
    public int biYPelsPerMeter;
    public uint biClrUsed;
    public uint biClrImportant;
}

/// <summary>BITMAPINFO（wingdi.h）：头 + 1 个 RGBQUAD（32 位 BI_RGB 不使用颜色表），44 字节。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFO
{
    public BITMAPINFOHEADER bmiHeader;
    public uint bmiColors;
}

/// <summary>TRACKMOUSEEVENT（winuser.h）：x64 为 24 字节，x86 为 16 字节。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct TRACKMOUSEEVENT
{
    public uint cbSize;
    public uint dwFlags;
    public IntPtr hwndTrack;
    public uint dwHoverTime;
}

/// <summary>MINMAXINFO（winuser.h）：5 个 POINT，40 字节。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MINMAXINFO
{
    public POINT ptReserved;
    public POINT ptMaxSize;
    public POINT ptMaxPosition;
    public POINT ptMinTrackSize;
    public POINT ptMaxTrackSize;
}

/// <summary>MONITORINFO（winuser.h）：40 字节。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MONITORINFO
{
    public uint cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
}

/// <summary>COMPOSITIONFORM（imm.h）：28 字节。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct COMPOSITIONFORM
{
    public uint dwStyle;
    public POINT ptCurrentPos;
    public RECT rcArea;
}

/// <summary>CANDIDATEFORM（imm.h）：32 字节。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CANDIDATEFORM
{
    public uint dwIndex;
    public uint dwStyle;
    public POINT ptCurrentPos;
    public RECT rcArea;
}
