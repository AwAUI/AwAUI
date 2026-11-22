using System;
using System.Runtime.InteropServices;
using AwAUI.Graphics;
using AwAUI.MacOS.Interop;
using static AwAUI.MacOS.Interop.ObjC;

namespace AwAUI.MacOS;

/// <summary>
/// 框架位图（预乘 RGBA8）与 CoreGraphics / AppKit 图片对象之间的转换：窗口图标（<c>NSImage</c>）与剪贴板图片。
/// 位图像素被复制到非托管缓冲，由 CoreGraphics 的 data provider 在图片释放时回收，因此调用方可以在返回后立即释放自己的位图。
/// </summary>
internal static unsafe class MacImages
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void ReleaseBufferFn(IntPtr info, IntPtr data, UIntPtr size);

    static readonly ReleaseBufferFn s_release = ReleaseBuffer;

    // 委托保存在静态字段中，CoreGraphics 持有的函数指针在进程内一直有效
    static readonly IntPtr s_releasePointer = Marshal.GetFunctionPointerForDelegate(s_release);

    static void ReleaseBuffer(IntPtr info, IntPtr data, UIntPtr size)
    {
        if (data != IntPtr.Zero) Marshal.FreeHGlobal(data);
    }

    /// <summary>创建 +1 引用的 <c>CGImageRef</c>；失败时返回 0。调用方负责 <see cref="CG.CGImageRelease"/>。</summary>
    public static IntPtr CreateCGImage(Bitmap bitmap)
    {
        if (bitmap is null) throw new ArgumentNullException(nameof(bitmap));
        var size = bitmap.Size;
        if (size.IsEmpty) return IntPtr.Zero;
        long length = (long)bitmap.RowBytes * size.Height;
        if (length <= 0) return IntPtr.Zero;

        var buffer = Marshal.AllocHGlobal(new IntPtr(length));
        try
        {
            Buffer.MemoryCopy((void*)bitmap.Address, (void*)buffer, length, length);
        }
        finally
        {
            GC.KeepAlive(bitmap);
        }

        var provider = CG.CGDataProviderCreateWithData(IntPtr.Zero, buffer, (UIntPtr)(ulong)length, s_releasePointer);
        if (provider == IntPtr.Zero)
        {
            Marshal.FreeHGlobal(buffer);
            return IntPtr.Zero;
        }
        var image = CG.CGImageCreate(
            (UIntPtr)(uint)size.Width, (UIntPtr)(uint)size.Height, (UIntPtr)8u, (UIntPtr)32u,
            (UIntPtr)(uint)bitmap.RowBytes, MacWindow.SrgbColorSpace, CG.AlphaPremultipliedLast,
            provider, IntPtr.Zero, 0, 0);
        CG.CGDataProviderRelease(provider); // 图片持有自己的引用；失败时这里释放缓冲
        return image;
    }

    /// <summary>创建 +1 引用的 <c>NSImage</c>（用于应用图标 / 最小化图标）；失败时返回 0。</summary>
    public static IntPtr CreateNSImage(Bitmap bitmap)
    {
        var image = CreateCGImage(bitmap);
        if (image == IntPtr.Zero) return IntPtr.Zero;
        var nsImage = Send(Send(Class("NSImage"), Sel("alloc")), Sel("initWithCGImage:size:"),
            image, new NSSize(bitmap.Width, bitmap.Height));
        CG.CGImageRelease(image); // NSImage 保留 CGImage（initWithCGImage:size: 的约定）
        return nsImage;
    }
}
