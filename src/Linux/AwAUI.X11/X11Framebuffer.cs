using System;
using System.Runtime.InteropServices;
using AwAUI.Graphics;
using AwAUI.X11.Interop;
using static AwAUI.X11.Interop.Xlib;

namespace AwAUI.X11;

/// <summary>
/// 软件呈现的帧缓冲与 XImage。三种形态：
/// <list type="bullet">
/// <item>MIT-SHM：帧缓冲就是 System V 共享内存段，XShmPutImage 零拷贝呈现（本地显示、32 bpp 直写格式时）；</item>
/// <item>直写：帧缓冲是普通内存，XImage 直接指向它，XPutImage 经套接字传输；</item>
/// <item>转换：视觉不是 8 位通道的 32 bpp（例如 16 位 565），渲染器写 Bgra8888，呈现前转换到第二块缓冲。</item>
/// </list>
/// XImage 结构由我们分配、经 XInitImage 初始化，从不交给 XDestroyImage（它会 free 掉 data）。
/// </summary>
internal sealed unsafe class X11Framebuffer : IDisposable
{
    readonly X11Platform _platform;
    readonly X11Visual _visual;
    readonly ulong _drawable;
    readonly IntPtr _gc;

    XImage* _image;
    XShmSegmentInfo* _shm;     // 非空表示当前缓冲是共享内存段
    IntPtr _pixels;            // 渲染器写入的缓冲
    IntPtr _converted;         // 转换形态下 XImage 指向的缓冲
    bool _dirty;               // 转换形态：帧缓冲已更新，尚未转换
    bool _shmBusy;             // 已发出 XShmPutImage，服务器可能仍在读取共享内存

    public X11Framebuffer(X11Platform platform, ulong drawable, IntPtr gc)
    {
        _platform = platform;
        _visual = platform.Visual;
        _drawable = drawable;
        _gc = gc;
    }

    public PixelSize Size { get; private set; }
    public int RowBytes => Size.Width * 4;
    public IntPtr Address => _pixels;
    public PixelFormat Format => _visual.DirectFormat ?? PixelFormat.Bgra8888;

    /// <summary>是否已经有过一帧内容（Expose 时可直接重新呈现）。</summary>
    public bool HasContent { get; private set; }

    /// <summary>当前缓冲是否使用 MIT-SHM。</summary>
    public bool UsesSharedMemory => _shm != null;

    /// <summary>准备好指定尺寸的缓冲，并确保服务器已不再读取它，之后渲染器可以写入。</summary>
    public void Prepare(PixelSize size)
    {
        if (size.IsEmpty) size = new PixelSize(1, 1);
        if (_image == null || size != Size)
        {
            Release();
            Allocate(size);
        }
        else if (_shmBusy)
        {
            // 上一次 XShmPutImage 的完成事件还没到：同步一次，服务器按序处理请求，返回时已读完共享内存
            XSync(_platform.Display, 0);
            _shmBusy = false;
        }
    }

    /// <summary>渲染器写完一帧。</summary>
    public void Commit()
    {
        if (_image == null) return;
        HasContent = true;
        _dirty = true;
    }

    /// <summary>收到本窗口的 XShmCompletionEvent。</summary>
    public void OnShmCompletion() => _shmBusy = false;

    /// <summary>把帧缓冲中的矩形（设备像素）呈现到窗口同一位置。</summary>
    public void Present(int x, int y, int width, int height)
    {
        if (_image == null || !HasContent) return;
        // 裁剪到缓冲范围
        if (x < 0) { width += x; x = 0; }
        if (y < 0) { height += y; y = 0; }
        if (x + width > Size.Width) width = Size.Width - x;
        if (y + height > Size.Height) height = Size.Height - y;
        if (width <= 0 || height <= 0) return;

        if (_converted != IntPtr.Zero && _dirty)
        {
            _visual.Convert((byte*)_pixels, RowBytes, (byte*)_converted, _image->bytes_per_line, Size.Width, Size.Height);
        }
        _dirty = false;

        if (_shm != null)
        {
            XShm.XShmPutImage(_platform.Display, _drawable, _gc, _image, x, y, x, y, (uint)width, (uint)height, 1);
            _shmBusy = true;
        }
        else
        {
            XPutImage(_platform.Display, _drawable, _gc, _image, x, y, x, y, (uint)width, (uint)height);
        }
    }

    void Allocate(PixelSize size)
    {
        int width = size.Width, height = size.Height;
        int bpp = _visual.BitsPerPixel;
        bool direct = _visual.DirectFormat != null;

        _image = (XImage*)Marshal.AllocHGlobal(sizeof(XImage));
        *_image = new XImage
        {
            width = width,
            height = height,
            xoffset = 0,
            format = ZPixmap,
            byte_order = LSBFirst,      // 像素按小端 32/16 位字排列；与服务器不同时 Xlib 负责交换
            bitmap_unit = 32,
            bitmap_bit_order = LSBFirst,
            bitmap_pad = 32,
            depth = _visual.Depth,
            bytes_per_line = ((width * bpp + 31) / 32) * 4,
            bits_per_pixel = bpp,
            red_mask = _visual.RedMask,
            green_mask = _visual.GreenMask,
            blue_mask = _visual.BlueMask,
        };
        long imageBytes = (long)_image->bytes_per_line * height;

        if (direct && _platform.ShmAvailable && TryAllocateShm(imageBytes))
        {
            _pixels = _shm->shmaddr;
        }
        else if (direct)
        {
            _pixels = AllocZeroed(imageBytes);
        }
        else
        {
            _pixels = AllocZeroed((long)width * 4 * height);
            _converted = AllocZeroed(imageBytes);
        }
        _image->data = _converted != IntPtr.Zero ? _converted : _pixels;
        if (XInitImage(_image) == 0)
        {
            Release();
            throw new InvalidOperationException($"XInitImage rejected a {width}x{height} image (depth {_visual.Depth}, {bpp} bpp).");
        }
        Size = size;
        HasContent = false;
        _dirty = false;
    }

    bool TryAllocateShm(long bytes)
    {
        var display = _platform.Display;
        int id = LibC.shmget(LibC.IPC_PRIVATE, (UIntPtr)(ulong)bytes, LibC.IPC_CREAT | 0x180 /* 0600 */);
        if (id < 0) { _platform.DisableShm("shmget failed"); return false; }
        var address = LibC.shmat(id, IntPtr.Zero, 0);
        if (address == new IntPtr(-1))
        {
            LibC.shmctl(id, LibC.IPC_RMID, null);
            _platform.DisableShm("shmat failed");
            return false;
        }

        var info = (XShmSegmentInfo*)Marshal.AllocHGlobal(sizeof(XShmSegmentInfo));
        *info = new XShmSegmentInfo { shmid = id, shmaddr = address, readOnly = 0 };

        // 远程显示、不同 IPC 命名空间（容器）或权限不足时服务器回 BadAccess：捕获错误并回退
        XErrors.Trap();
        int ok = XShm.XShmAttach(display, info);
        XSync(display, 0);
        int error = XErrors.Untrap();
        // 双方都附加后立即标记删除：最后一方分离时内核回收，进程崩溃也不会泄漏段
        LibC.shmctl(id, LibC.IPC_RMID, null);
        if (ok == 0 || error != 0)
        {
            LibC.shmdt(address);
            Marshal.FreeHGlobal((IntPtr)info);
            _platform.DisableShm($"XShmAttach failed (X error {error})");
            return false;
        }
        new Span<byte>((void*)address, checked((int)bytes)).Clear();
        _image->obdata = (IntPtr)info; // XShmPutImage 从 obdata 取 XShmSegmentInfo
        _shm = info;
        return true;
    }

    static IntPtr AllocZeroed(long bytes)
    {
        var p = Marshal.AllocHGlobal(new IntPtr(bytes));
        new Span<byte>((void*)p, checked((int)bytes)).Clear();
        return p;
    }

    void Release()
    {
        if (_shm != null)
        {
            // 服务器有自己的映射：分离请求按序在已排队的 XShmPutImage 之后处理，客户端可立即 shmdt
            XShm.XShmDetach(_platform.Display, _shm);
            LibC.shmdt(_shm->shmaddr);
            Marshal.FreeHGlobal((IntPtr)_shm);
            _shm = null;
            _pixels = IntPtr.Zero;
        }
        if (_pixels != IntPtr.Zero) { Marshal.FreeHGlobal(_pixels); _pixels = IntPtr.Zero; }
        if (_converted != IntPtr.Zero) { Marshal.FreeHGlobal(_converted); _converted = IntPtr.Zero; }
        if (_image != null) { Marshal.FreeHGlobal((IntPtr)_image); _image = null; }
        _shmBusy = false;
        Size = default;
        HasContent = false;
    }

    public void Dispose() => Release();
}
