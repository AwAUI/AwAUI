using System;
using System.Runtime.InteropServices;

namespace AwAUI.X11.Interop;

// Xlib 结构体的 blittable 布局，只针对 LP64（x86_64 / arm64 Linux）：
// long / unsigned long / XID / Atom / Time / Window / 指针均为 8 字节，int / Bool 为 4 字节。
// 32 位（ILP32）布局不同，平台构造时拒绝（见 X11Platform）。
// 字段偏移与大小在注释里按 Xlib.h / Xutil.h / XShm.h 逐项标出，由 scratch 检查程序以 Marshal.OffsetOf 核对。

/// <summary>XAnyEvent：所有事件共有的头部（40 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XAnyEvent
{
    public int type;            // 0
    public ulong serial;        // 8
    public int send_event;      // 16 (Bool)
    public IntPtr display;      // 24
    public ulong window;        // 32
}

/// <summary>XKeyEvent（KeyPress / KeyRelease，96 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XKeyEvent
{
    public int type;            // 0
    public ulong serial;        // 8
    public int send_event;      // 16
    public IntPtr display;      // 24
    public ulong window;        // 32
    public ulong root;          // 40
    public ulong subwindow;     // 48
    public ulong time;          // 56 (Time，毫秒，服务器时间)
    public int x, y;            // 64, 68
    public int x_root, y_root;  // 72, 76
    public uint state;          // 80
    public uint keycode;        // 84
    public int same_screen;     // 88
}

/// <summary>XButtonEvent（ButtonPress / ButtonRelease，96 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XButtonEvent
{
    public int type;            // 0
    public ulong serial;        // 8
    public int send_event;      // 16
    public IntPtr display;      // 24
    public ulong window;        // 32
    public ulong root;          // 40
    public ulong subwindow;     // 48
    public ulong time;          // 56
    public int x, y;            // 64, 68
    public int x_root, y_root;  // 72, 76
    public uint state;          // 80
    public uint button;         // 84
    public int same_screen;     // 88
}

/// <summary>XMotionEvent（MotionNotify，96 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XMotionEvent
{
    public int type;            // 0
    public ulong serial;        // 8
    public int send_event;      // 16
    public IntPtr display;      // 24
    public ulong window;        // 32
    public ulong root;          // 40
    public ulong subwindow;     // 48
    public ulong time;          // 56
    public int x, y;            // 64, 68
    public int x_root, y_root;  // 72, 76
    public uint state;          // 80
    public byte is_hint;        // 84 (char)
    public int same_screen;     // 88
}

/// <summary>XCrossingEvent（EnterNotify / LeaveNotify，104 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XCrossingEvent
{
    public int type;            // 0
    public ulong serial;        // 8
    public int send_event;      // 16
    public IntPtr display;      // 24
    public ulong window;        // 32
    public ulong root;          // 40
    public ulong subwindow;     // 48
    public ulong time;          // 56
    public int x, y;            // 64, 68
    public int x_root, y_root;  // 72, 76
    public int mode;            // 80
    public int detail;          // 84
    public int same_screen;     // 88
    public int focus;           // 92
    public uint state;          // 96
}

/// <summary>XFocusChangeEvent（FocusIn / FocusOut，48 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XFocusChangeEvent
{
    public int type;            // 0
    public ulong serial;        // 8
    public int send_event;      // 16
    public IntPtr display;      // 24
    public ulong window;        // 32
    public int mode;            // 40
    public int detail;          // 44
}

/// <summary>XExposeEvent（Expose，64 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XExposeEvent
{
    public int type;            // 0
    public ulong serial;        // 8
    public int send_event;      // 16
    public IntPtr display;      // 24
    public ulong window;        // 32
    public int x, y;            // 40, 44
    public int width, height;   // 48, 52
    public int count;           // 56
}

/// <summary>XConfigureEvent（ConfigureNotify，88 字节）。注意第 32 字节处是 event，窗口在 40。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XConfigureEvent
{
    public int type;            // 0
    public ulong serial;        // 8
    public int send_event;      // 16
    public IntPtr display;      // 24
    public ulong @event;        // 32
    public ulong window;        // 40
    public int x, y;            // 48, 52
    public int width, height;   // 56, 60
    public int border_width;    // 64
    public ulong above;         // 72
    public int override_redirect; // 80
}

/// <summary>XClientMessageEvent（ClientMessage，96 字节）。data 为 char[20]/short[10]/long[5] 的联合，这里按 long[5] 读。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XClientMessageEvent
{
    public int type;            // 0
    public ulong serial;        // 8
    public int send_event;      // 16
    public IntPtr display;      // 24
    public ulong window;        // 32
    public ulong message_type;  // 40 (Atom)
    public int format;          // 48
    public long l0, l1, l2, l3, l4; // 56..95
}

/// <summary>
/// XEvent：Xlib.h 中为含 <c>long pad[24]</c> 的联合，LP64 上 192 字节。
/// 只声明我们读取的成员；其余事件只看 <see cref="type"/> 与 <see cref="xany"/>。
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 192)]
internal struct XEvent
{
    [FieldOffset(0)] public int type;
    [FieldOffset(0)] public XAnyEvent xany;
    [FieldOffset(0)] public XKeyEvent xkey;
    [FieldOffset(0)] public XButtonEvent xbutton;
    [FieldOffset(0)] public XMotionEvent xmotion;
    [FieldOffset(0)] public XCrossingEvent xcrossing;
    [FieldOffset(0)] public XFocusChangeEvent xfocus;
    [FieldOffset(0)] public XExposeEvent xexpose;
    [FieldOffset(0)] public XConfigureEvent xconfigure;
    [FieldOffset(0)] public XClientMessageEvent xclient;
    [FieldOffset(0)] public XSelectionRequestEvent xselectionrequest;
    [FieldOffset(0)] public XSelectionEvent xselection;
    [FieldOffset(0)] public XSelectionClearEvent xselectionclear;
    [FieldOffset(0)] public XPropertyEvent xproperty;
}

/// <summary>XSelectionRequestEvent（80 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XSelectionRequestEvent
{
    public int type;            // 0
    public ulong serial;        // 8
    public int send_event;      // 16
    public IntPtr display;      // 24
    public ulong owner;         // 32
    public ulong requestor;     // 40
    public ulong selection;     // 48
    public ulong target;        // 56
    public ulong property;      // 64
    public ulong time;          // 72
}

/// <summary>XSelectionEvent（SelectionNotify，72 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XSelectionEvent
{
    public int type;            // 0
    public ulong serial;        // 8
    public int send_event;      // 16
    public IntPtr display;      // 24
    public ulong requestor;     // 32
    public ulong selection;     // 40
    public ulong target;        // 48
    public ulong property;      // 56
    public ulong time;          // 64
}

/// <summary>XSelectionClearEvent（56 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XSelectionClearEvent
{
    public int type;            // 0
    public ulong serial;        // 8
    public int send_event;      // 16
    public IntPtr display;      // 24
    public ulong window;        // 32
    public ulong selection;     // 40
    public ulong time;          // 48
}

/// <summary>XPropertyEvent（PropertyNotify，64 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XPropertyEvent
{
    public int type;            // 0
    public ulong serial;        // 8
    public int send_event;      // 16
    public IntPtr display;      // 24
    public ulong window;        // 32
    public ulong atom;          // 40
    public ulong time;          // 48
    public int state;           // 56（PropertyNewValue = 0，PropertyDelete = 1）
}

/// <summary>XIMCallback（16 字节）：输入法回调的客户数据与函数指针。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XIMCallback
{
    public IntPtr client_data;
    public IntPtr callback;
}

/// <summary>XIMText（32 字节）。length 是字符数；string 为多字节（区域设置编码）或 wchar_t（UTF-32）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XIMText
{
    public ushort length;       // 0
    public ulong* feedback;     // 8 (XIMFeedback*，unsigned long)
    public int encoding_is_wchar; // 16
    public IntPtr @string;      // 24
}

/// <summary>XIMPreeditDrawCallbackStruct（24 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XIMPreeditDrawCallbackStruct
{
    public int caret;           // 0
    public int chg_first;       // 4
    public int chg_length;      // 8
    public XIMText* text;       // 16
}

/// <summary>XIMPreeditCaretCallbackStruct（12 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XIMPreeditCaretCallbackStruct
{
    public int position;        // 0
    public int direction;       // 4
    public int style;           // 8
}

/// <summary>XIMStyles（16 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XIMStyles
{
    public ushort count_styles;     // 0
    public ulong* supported_styles; // 8 (XIMStyle = unsigned long)
}

/// <summary>XPoint（4 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XPoint
{
    public short x, y;
}

/// <summary>XErrorEvent（40 字节）。错误处理回调以指针形式收到。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XErrorEvent
{
    public int type;            // 0
    public IntPtr display;      // 8
    public ulong resourceid;    // 16 (XID)
    public ulong serial;        // 24
    public byte error_code;     // 32
    public byte request_code;   // 33
    public byte minor_code;     // 34
}

/// <summary>XSetWindowAttributes（112 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XSetWindowAttributes
{
    public ulong background_pixmap;     // 0 (Pixmap)
    public ulong background_pixel;      // 8
    public ulong border_pixmap;         // 16
    public ulong border_pixel;          // 24
    public int bit_gravity;             // 32
    public int win_gravity;             // 36
    public int backing_store;           // 40
    public ulong backing_planes;        // 48
    public ulong backing_pixel;         // 56
    public int save_under;              // 64 (Bool)
    public long event_mask;             // 72
    public long do_not_propagate_mask;  // 80
    public int override_redirect;       // 88 (Bool)
    public ulong colormap;              // 96
    public ulong cursor;                // 104
}

/// <summary>XVisualInfo（Xutil.h，64 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XVisualInfo
{
    public IntPtr visual;       // 0 (Visual*)
    public ulong visualid;      // 8
    public int screen;          // 16
    public int depth;           // 20
    public int @class;          // 24
    public ulong red_mask;      // 32
    public ulong green_mask;    // 40
    public ulong blue_mask;     // 48
    public int colormap_size;   // 56
    public int bits_per_rgb;    // 60
}

/// <summary>XPixmapFormatValues（12 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XPixmapFormatValues
{
    public int depth;           // 0
    public int bits_per_pixel;  // 4
    public int scanline_pad;    // 8
}

/// <summary>
/// XImage（Xlib.h，136 字节）。我们自己分配并填写前半部分，再由 XInitImage 填入 <c>f</c> 中的函数指针，
/// 因而无需 XCreateImage / XDestroyImage（后者在多数版本中只是宏，且会 free 掉 data）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XImage
{
    public int width, height;   // 0, 4
    public int xoffset;         // 8
    public int format;          // 12
    public IntPtr data;         // 16 (char*)
    public int byte_order;      // 24
    public int bitmap_unit;     // 28
    public int bitmap_bit_order;// 32
    public int bitmap_pad;      // 36
    public int depth;           // 40
    public int bytes_per_line;  // 44
    public int bits_per_pixel;  // 48
    public ulong red_mask;      // 56
    public ulong green_mask;    // 64
    public ulong blue_mask;     // 72
    public IntPtr obdata;       // 80 (XPointer)
    // struct funcs：create_image, destroy_image, get_pixel, put_pixel, sub_image, add_pixel
    public IntPtr f_create_image;   // 88
    public IntPtr f_destroy_image;  // 96
    public IntPtr f_get_pixel;      // 104
    public IntPtr f_put_pixel;      // 112
    public IntPtr f_sub_image;      // 120
    public IntPtr f_add_pixel;      // 128
}

/// <summary>XSizeHints（Xutil.h，80 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XSizeHints
{
    public long flags;                      // 0
    public int x, y;                        // 8, 12
    public int width, height;               // 16, 20
    public int min_width, min_height;       // 24, 28
    public int max_width, max_height;       // 32, 36
    public int width_inc, height_inc;       // 40, 44
    public int min_aspect_x, min_aspect_y;  // 48, 52
    public int max_aspect_x, max_aspect_y;  // 56, 60
    public int base_width, base_height;     // 64, 68
    public int win_gravity;                 // 72
}

/// <summary>XColor（16 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XColor
{
    public ulong pixel;         // 0
    public ushort red, green, blue; // 8, 10, 12
    public byte flags;          // 14
    public byte pad;            // 15
}

/// <summary>XShmSegmentInfo（XShm.h，32 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XShmSegmentInfo
{
    public ulong shmseg;        // 0 (ShmSeg = XID)
    public int shmid;           // 8
    public IntPtr shmaddr;      // 16
    public int readOnly;        // 24 (Bool)
}

/// <summary>struct pollfd（8 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PollFd
{
    public int fd;              // 0
    public short events;        // 4
    public short revents;       // 6
}

/// <summary>struct timespec（LP64，16 字节）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Timespec
{
    public long tv_sec;         // 0
    public long tv_nsec;        // 8
}
