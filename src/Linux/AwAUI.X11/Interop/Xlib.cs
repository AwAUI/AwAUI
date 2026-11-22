using System;
using System.Runtime.InteropServices;

namespace AwAUI.X11.Interop;

/// <summary>
/// libX11 的裸互操作（design/11）：所有签名只用 blittable 类型——字符串为以 '\0' 结尾的 UTF-8/Latin-1 <c>byte*</c>，
/// Bool / Status 为 int，XID（Window、Atom、Cursor、Pixmap、Colormap、VisualID、Time）为 ulong，
/// Display* / Visual* / GC / XIM / XIC 为 IntPtr。只支持 LP64（unsigned long 为 8 字节）。
/// </summary>
internal static unsafe class Xlib
{
    const string Lib = "libX11.so.6";

    // ------------------------------------------------------------ 连接与屏幕
    [DllImport(Lib, ExactSpelling = true)] public static extern int XInitThreads();
    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr XOpenDisplay(byte* name);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XCloseDisplay(IntPtr display);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XConnectionNumber(IntPtr display);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XDefaultScreen(IntPtr display);
    [DllImport(Lib, ExactSpelling = true)] public static extern ulong XRootWindow(IntPtr display, int screen);
    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr XDefaultVisual(IntPtr display, int screen);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XDefaultDepth(IntPtr display, int screen);
    [DllImport(Lib, ExactSpelling = true)] public static extern ulong XDefaultColormap(IntPtr display, int screen);
    [DllImport(Lib, ExactSpelling = true)] public static extern ulong XBlackPixel(IntPtr display, int screen);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XImageByteOrder(IntPtr display);
    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr XResourceManagerString(IntPtr display);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XScreenCount(IntPtr display);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XDisplayWidth(IntPtr display, int screen);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XDisplayHeight(IntPtr display, int screen);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XFree(void* data);

    // ------------------------------------------------------------ 视觉与颜色表
    [DllImport(Lib, ExactSpelling = true)] public static extern ulong XVisualIDFromVisual(IntPtr visual);
    [DllImport(Lib, ExactSpelling = true)]
    public static extern XVisualInfo* XGetVisualInfo(IntPtr display, long vinfoMask, XVisualInfo* template, int* nitems);
    [DllImport(Lib, ExactSpelling = true)]
    public static extern int XMatchVisualInfo(IntPtr display, int screen, int depth, int @class, XVisualInfo* vinfo);
    [DllImport(Lib, ExactSpelling = true)] public static extern XPixmapFormatValues* XListPixmapFormats(IntPtr display, int* count);
    [DllImport(Lib, ExactSpelling = true)] public static extern ulong XCreateColormap(IntPtr display, ulong window, IntPtr visual, int alloc);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XFreeColormap(IntPtr display, ulong colormap);

    // ------------------------------------------------------------ 窗口
    [DllImport(Lib, ExactSpelling = true)]
    public static extern ulong XCreateWindow(IntPtr display, ulong parent, int x, int y, uint width, uint height, uint borderWidth,
        int depth, uint @class, IntPtr visual, ulong valueMask, XSetWindowAttributes* attributes);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XDestroyWindow(IntPtr display, ulong window);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XMapWindow(IntPtr display, ulong window);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XUnmapWindow(IntPtr display, ulong window);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XMoveWindow(IntPtr display, ulong window, int x, int y);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XResizeWindow(IntPtr display, ulong window, uint width, uint height);
    [DllImport(Lib, ExactSpelling = true)]
    public static extern int XMoveResizeWindow(IntPtr display, ulong window, int x, int y, uint width, uint height);
    [DllImport(Lib, ExactSpelling = true)] public static extern ulong XInternAtom(IntPtr display, byte* name, int onlyIfExists);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XSetWMProtocols(IntPtr display, ulong window, ulong* protocols, int count);
    [DllImport(Lib, ExactSpelling = true)]
    public static extern int XChangeProperty(IntPtr display, ulong window, ulong property, ulong type, int format, int mode,
        byte* data, int nelements);
    [DllImport(Lib, ExactSpelling = true)] public static extern void XSetWMNormalHints(IntPtr display, ulong window, XSizeHints* hints);
    /// <summary>Bool XTranslateCoordinates(Display*, Window, Window, int, int, int*, int*, Window*)：坐标换算出错（例如跨屏幕）时返回 0。</summary>
    [DllImport(Lib, ExactSpelling = true)]
    public static extern int XTranslateCoordinates(IntPtr display, ulong srcWindow, ulong destWindow, int srcX, int srcY,
        int* destX, int* destY, ulong* child);

    // ------------------------------------------------------------ 绘制
    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr XCreateGC(IntPtr display, ulong drawable, ulong valueMask, void* values);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XFreeGC(IntPtr display, IntPtr gc);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XInitImage(XImage* image);
    [DllImport(Lib, ExactSpelling = true)]
    public static extern int XPutImage(IntPtr display, ulong drawable, IntPtr gc, XImage* image, int srcX, int srcY,
        int destX, int destY, uint width, uint height);

    // ------------------------------------------------------------ 事件
    [DllImport(Lib, ExactSpelling = true)] public static extern int XPending(IntPtr display);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XEventsQueued(IntPtr display, int mode);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XNextEvent(IntPtr display, XEvent* ev);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XPeekEvent(IntPtr display, XEvent* ev);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XFlush(IntPtr display);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XSync(IntPtr display, int discard);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XFilterEvent(XEvent* ev, ulong window);
    /// <summary>参数实为 XMappingEvent*，它是 XEvent 联合的成员，直接传 XEvent*。</summary>
    [DllImport(Lib, ExactSpelling = true)] public static extern int XRefreshKeyboardMapping(XEvent* ev);

    // ------------------------------------------------------------ 错误
    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr XSetErrorHandler(IntPtr handler);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XGetErrorText(IntPtr display, int code, byte* buffer, int length);

    // ------------------------------------------------------------ 光标
    [DllImport(Lib, ExactSpelling = true)] public static extern ulong XCreateFontCursor(IntPtr display, uint shape);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XDefineCursor(IntPtr display, ulong window, ulong cursor);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XFreeCursor(IntPtr display, ulong cursor);
    [DllImport(Lib, ExactSpelling = true)]
    public static extern ulong XCreateBitmapFromData(IntPtr display, ulong drawable, byte* data, uint width, uint height);
    [DllImport(Lib, ExactSpelling = true)]
    public static extern ulong XCreatePixmapCursor(IntPtr display, ulong source, ulong mask, XColor* foreground, XColor* background, uint x, uint y);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XFreePixmap(IntPtr display, ulong pixmap);

    // ------------------------------------------------------------ 键盘与输入法
    [DllImport(Lib, ExactSpelling = true)] public static extern int XkbSetDetectableAutoRepeat(IntPtr display, int detectable, int* supported);
    [DllImport(Lib, ExactSpelling = true)]
    public static extern int XLookupString(XKeyEvent* ev, byte* buffer, int bytes, ulong* keysym, void* composeStatus);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XSupportsLocale();
    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr XSetLocaleModifiers(byte* modifiers);
    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr XOpenIM(IntPtr display, IntPtr db, byte* resName, byte* resClass);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XCloseIM(IntPtr im);

    /// <summary>
    /// XIC XCreateIC(XIM im, ...)：可变参数函数，以「名称, 值」对结尾 NULL。这里固定为三对参数 + 终止符的形状。
    /// 参数全部为指针宽度的整数：LP64 System V（x86_64）与 AAPCS64（Linux arm64）对这类可变参数的传递与定长参数一致
    /// （x86_64 上被调方只用 %al 决定是否保存向量寄存器，整数参数不受影响）。
    /// </summary>
    [DllImport(Lib, ExactSpelling = true)]
    public static extern IntPtr XCreateIC(IntPtr im, byte* name1, IntPtr value1, byte* name2, IntPtr value2,
        byte* name3, IntPtr value3, IntPtr terminator);
    [DllImport(Lib, ExactSpelling = true)] public static extern void XDestroyIC(IntPtr ic);
    [DllImport(Lib, ExactSpelling = true)] public static extern void XSetICFocus(IntPtr ic);
    [DllImport(Lib, ExactSpelling = true)] public static extern void XUnsetICFocus(IntPtr ic);
    [DllImport(Lib, ExactSpelling = true)]
    public static extern int Xutf8LookupString(IntPtr ic, XKeyEvent* ev, byte* buffer, int bytes, ulong* keysym, int* status);

    /// <summary>XIC 的四对参数形状（XNInputStyle、XNClientWindow、XNFocusWindow、XNPreeditAttributes）。</summary>
    [DllImport(Lib, ExactSpelling = true, EntryPoint = "XCreateIC")]
    public static extern IntPtr XCreateIC4(IntPtr im, byte* name1, IntPtr value1, byte* name2, IntPtr value2,
        byte* name3, IntPtr value3, byte* name4, IntPtr value4, IntPtr terminator);
    /// <summary>char* XGetIMValues(XIM, name, &value, NULL)：返回第一个失败的名字，成功时为 NULL。</summary>
    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr XGetIMValues(IntPtr im, byte* name, IntPtr* value, IntPtr terminator);
    /// <summary>char* XSetICValues(XIC, name, value, NULL)。</summary>
    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr XSetICValues(IntPtr ic, byte* name, IntPtr value, IntPtr terminator);
    /// <summary>XVaNestedList XVaCreateNestedList(int unused, name, value, NULL)。</summary>
    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr XVaCreateNestedList(int unused, byte* name1, IntPtr value1, IntPtr terminator);
    /// <summary>四对参数的形状（预编辑回调）。</summary>
    [DllImport(Lib, ExactSpelling = true, EntryPoint = "XVaCreateNestedList")]
    public static extern IntPtr XVaCreateNestedList4(int unused, byte* name1, IntPtr value1, byte* name2, IntPtr value2,
        byte* name3, IntPtr value3, byte* name4, IntPtr value4, IntPtr terminator);
    /// <summary>重置输入上下文（放弃预编辑）；返回值需 XFree。</summary>
    [DllImport(Lib, ExactSpelling = true)] public static extern IntPtr Xutf8ResetIC(IntPtr ic);

    // ------------------------------------------------------------ 选择（剪贴板）与属性
    [DllImport(Lib, ExactSpelling = true)] public static extern int XSetSelectionOwner(IntPtr display, ulong selection, ulong owner, ulong time);
    [DllImport(Lib, ExactSpelling = true)] public static extern ulong XGetSelectionOwner(IntPtr display, ulong selection);
    [DllImport(Lib, ExactSpelling = true)]
    public static extern int XConvertSelection(IntPtr display, ulong selection, ulong target, ulong property, ulong requestor, ulong time);
    [DllImport(Lib, ExactSpelling = true)]
    public static extern int XGetWindowProperty(IntPtr display, ulong window, ulong property, long offset, long length, int delete,
        ulong reqType, ulong* actualType, int* actualFormat, ulong* nitems, ulong* bytesAfter, byte** prop);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XDeleteProperty(IntPtr display, ulong window, ulong property);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XSendEvent(IntPtr display, ulong window, int propagate, long mask, XEvent* ev);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XSelectInput(IntPtr display, ulong window, long mask);
    [DllImport(Lib, ExactSpelling = true)] public static extern long XMaxRequestSize(IntPtr display);
    [DllImport(Lib, ExactSpelling = true)] public static extern long XExtendedMaxRequestSize(IntPtr display);

    // ------------------------------------------------------------ 常量（X.h / Xlib.h / Xutil.h / Xatom.h / cursorfont.h）

    // 事件类型
    public const int KeyPress = 2, KeyRelease = 3, ButtonPress = 4, ButtonRelease = 5, MotionNotify = 6,
        EnterNotify = 7, LeaveNotify = 8, FocusIn = 9, FocusOut = 10, Expose = 12, DestroyNotify = 17,
        UnmapNotify = 18, MapNotify = 19, ConfigureNotify = 22, PropertyNotify = 28, SelectionClear = 29, SelectionRequest = 30,
        SelectionNotify = 31, ClientMessage = 33, MappingNotify = 34;
    public const int PropertyNewValue = 0, PropertyDelete = 1;
    public const uint InputOnly = 2;

    // 事件掩码
    public const long KeyPressMask = 1L << 0, KeyReleaseMask = 1L << 1, ButtonPressMask = 1L << 2, ButtonReleaseMask = 1L << 3,
        EnterWindowMask = 1L << 4, LeaveWindowMask = 1L << 5, PointerMotionMask = 1L << 6, ExposureMask = 1L << 15,
        StructureNotifyMask = 1L << 17, SubstructureNotifyMask = 1L << 19, SubstructureRedirectMask = 1L << 20,
        FocusChangeMask = 1L << 21, PropertyChangeMask = 1L << 22;

    // 修饰键与按钮状态掩码
    public const uint ShiftMask = 1 << 0, LockMask = 1 << 1, ControlMask = 1 << 2, Mod1Mask = 1 << 3, Mod2Mask = 1 << 4,
        Mod4Mask = 1 << 6, Mod5Mask = 1 << 7;

    // 进入/离开与焦点通知的 mode / detail
    public const int NotifyNormal = 0, NotifyGrab = 1, NotifyUngrab = 2;
    public const int NotifyInferior = 2, NotifyPointer = 5;

    // XCreateWindow 的 valuemask（CW*）
    public const ulong CWBackPixmap = 1UL << 0, CWBorderPixel = 1UL << 3, CWBitGravity = 1UL << 4, CWEventMask = 1UL << 11,
        CWColormap = 1UL << 13;

    public const uint InputOutput = 1;
    public const int AllocNone = 0;
    public const int NorthWestGravity = 1;
    public const int TrueColor = 4;
    public const int ZPixmap = 2;
    public const int LSBFirst = 0, MSBFirst = 1;
    public const int PropModeReplace = 0;

    // ICCCM / EWMH：自绘标题栏（design/04）
    public const int IconicState = 3;
    public const long NetWmStateRemove = 0, NetWmStateAdd = 1;
    public const long NetWmMoveresizeMove = 8, NetWmMoveresizeSourceApplication = 1;
    public const long MwmHintsDecorations = 2;
    public const int QueuedAlready = 0, QueuedAfterReading = 1, QueuedAfterFlush = 2;

    // XGetVisualInfo 的模板掩码
    public const long VisualIDMask = 0x1, VisualScreenMask = 0x2;

    // XSizeHints.flags
    public const long USPosition = 1L << 0, USSize = 1L << 1, PPosition = 1L << 2, PSize = 1L << 3,
        PMinSize = 1L << 4, PMaxSize = 1L << 5;

    // 预定义原子（Xatom.h）
    public const ulong XA_ATOM = 4, XA_CARDINAL = 6, XA_INTEGER = 19, XA_STRING = 31, XA_WM_ICON_NAME = 37, XA_WM_NAME = 39, XA_WM_CLASS = 67;
    public const ulong AnyPropertyType = 0, CurrentTime = 0;

    // 光标字体（cursorfont.h）
    public const uint XC_X_cursor = 0, XC_crosshair = 34, XC_hand2 = 60, XC_left_ptr = 68,
        XC_sb_h_double_arrow = 108, XC_sb_v_double_arrow = 116, XC_watch = 150, XC_xterm = 152;

    // 输入法（Xlib.h）
    public const long XIMPreeditArea = 0x0001, XIMPreeditCallbacks = 0x0002, XIMPreeditPosition = 0x0004, XIMPreeditNothing = 0x0008,
        XIMPreeditNone = 0x0010, XIMStatusNothing = 0x0400, XIMStatusNone = 0x0800;
    public const int XBufferOverflow = -1, XLookupNone = 1, XLookupChars = 2, XLookupKeySym = 3, XLookupBoth = 4;

    /// <summary>以 '\0' 结尾的 ASCII 字节，供 byte* 参数使用（调用方 fixed 固定）。</summary>
    public static byte[] Z(string s)
    {
        var b = new byte[s.Length + 1];
        for (int i = 0; i < s.Length; i++) b[i] = (byte)s[i];
        return b;
    }

    public static ulong InternAtom(IntPtr display, string name)
    {
        var bytes = Z(name);
        fixed (byte* p = bytes) return XInternAtom(display, p, 0);
    }
}

/// <summary>MIT-SHM 扩展（libXext）。库或扩展不可用时调用方回退到 XPutImage。</summary>
internal static unsafe class XShm
{
    const string Lib = "libXext.so.6";

    /// <summary>XShmCompletionEvent 相对扩展事件基值的偏移（XShm.h: ShmCompletion）。</summary>
    public const int ShmCompletion = 0;

    [DllImport(Lib, ExactSpelling = true)] public static extern int XShmQueryExtension(IntPtr display);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XShmGetEventBase(IntPtr display);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XShmAttach(IntPtr display, XShmSegmentInfo* info);
    [DllImport(Lib, ExactSpelling = true)] public static extern int XShmDetach(IntPtr display, XShmSegmentInfo* info);
    [DllImport(Lib, ExactSpelling = true)]
    public static extern int XShmPutImage(IntPtr display, ulong drawable, IntPtr gc, XImage* image, int srcX, int srcY,
        int destX, int destY, uint width, uint height, int sendEvent);
}
