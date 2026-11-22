using System;
using System.Runtime.InteropServices;

namespace AwAUI.X11.Interop;

/// <summary>
/// XRandR（libXrandr）的最小互操作：只用于订阅显示器布局变化事件。几何本身仍由 Xlib 的
/// <c>XScreenCount</c> / <c>XDisplayWidth</c> / <c>XDisplayHeight</c> 与 <c>_NET_WORKAREA</c> 查询
/// （见 <see cref="AwAUI.X11.X11Screens"/>）；扩展不可用或 libXrandr 未安装时调用方静默退回单一显示器。
/// </summary>
internal static unsafe class Xrandr
{
    const string Lib = "libXrandr.so.2";

    /// <summary>RR 事件基值 + <see cref="ScreenChangeNotify"/> 即 RRScreenChangeNotify 的事件类型。</summary>
    public const int ScreenChangeNotify = 0;

    /// <summary>RRScreenChangeNotifyMask（randr.h）。</summary>
    public const int ScreenChangeNotifyMask = 1;

    [DllImport(Lib, ExactSpelling = true)]
    public static extern int XRRQueryExtension(IntPtr display, out int eventBase, out int errorBase);

    [DllImport(Lib, ExactSpelling = true)]
    public static extern void XRRSelectInput(IntPtr display, ulong window, int mask);

    /// <summary>用事件里的新布局更新 Xlib 的屏幕尺寸缓存；返回是否更新。</summary>
    [DllImport(Lib, ExactSpelling = true)]
    public static extern int XRRUpdateConfiguration(XEvent* ev);
}
