using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using AwAUI.Render.Rhi;

namespace AwAUI.Rhi.Metal.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct CGSize { public double Width, Height; public CGSize(double w, double h) { Width = w; Height = h; } }

[StructLayout(LayoutKind.Sequential)]
internal struct MTLSize
{
    public ulong Width, Height, Depth;
    public MTLSize(int w, int h) { Width = (ulong)w; Height = (ulong)h; Depth = 1; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct MTLOrigin
{
    public ulong X, Y, Z;
    public MTLOrigin(int x, int y) { X = (ulong)x; Y = (ulong)y; Z = 0; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct MTLClearColor { public double Red, Green, Blue, Alpha; }

[StructLayout(LayoutKind.Sequential)]
internal struct MTLScissorRect { public ulong X, Y, Width, Height; }

/// <summary>
/// Objective-C 运行时与 Metal 的裸互操作（与 AwAUI.MacOS 同一风格，design/04 D13、design/11）。
/// 签名只用 blittable 类型，同一份声明可用于 netstandard2.0 与 NativeAOT。objc_msgSend 按调用约定声明多个重载，入口点相同；
/// 结构体参数（MTLSize、MTLClearColor 等）按平台 ABI 传递，由运行时处理（arm64 的 HFA 与大结构体按引用传递）。
/// 没有返回大结构体的消息，因此不需要 objc_msgSend_stret。
/// </summary>
internal static unsafe class ObjC
{
    const string Lib = "/usr/lib/libobjc.A.dylib";
    const string LibSystem = "/usr/lib/libSystem.dylib";
    public const string MetalFramework = "/System/Library/Frameworks/Metal.framework/Metal";
    public const string CoreGraphicsFramework = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    public const string QuartzCoreFramework = "/System/Library/Frameworks/QuartzCore.framework/QuartzCore";

    [DllImport(Lib)] static extern IntPtr objc_getClass(byte* name);
    [DllImport(Lib)] static extern IntPtr sel_registerName(byte* name);
    [DllImport(Lib)] public static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(Lib)] public static extern void objc_autoreleasePoolPop(IntPtr pool);
    [DllImport(LibSystem)] static extern IntPtr dlopen(byte* path, int mode);

    [DllImport(MetalFramework)] public static extern IntPtr MTLCreateSystemDefaultDevice();

    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a, IntPtr b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a, IntPtr b, IntPtr c);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, ulong a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, ulong a, ulong b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, ulong a, ulong b, ulong c, byte d);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, char* chars, ulong length);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(IntPtr self, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(IntPtr self, IntPtr sel, IntPtr a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(IntPtr self, IntPtr sel, ulong a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(IntPtr self, IntPtr sel, IntPtr a, ulong b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(IntPtr self, IntPtr sel, IntPtr a, ulong b, ulong c);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(IntPtr self, IntPtr sel, void* a, ulong b, ulong c);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(IntPtr self, IntPtr sel, ulong a, ulong b, ulong c, ulong d, ulong e);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendBool(IntPtr self, IntPtr sel, byte a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendSize(IntPtr self, IntPtr sel, CGSize a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendClearColor(IntPtr self, IntPtr sel, MTLClearColor a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendScissor(IntPtr self, IntPtr sel, MTLScissorRect a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern ulong SendULong(IntPtr self, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern double SendDouble(IntPtr self, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern byte SendByte(IntPtr self, IntPtr sel, ulong a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern byte SendByte(IntPtr self, IntPtr sel);

    // copyFromBuffer:sourceOffset:sourceBytesPerRow:sourceBytesPerImage:sourceSize:toTexture:destinationSlice:destinationLevel:destinationOrigin:
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern void SendCopyBufferToTexture(IntPtr self, IntPtr sel, IntPtr buffer, ulong offset, ulong bytesPerRow, ulong bytesPerImage,
        MTLSize size, IntPtr texture, ulong slice, ulong level, MTLOrigin origin);

    // copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toTexture:destinationSlice:destinationLevel:destinationOrigin:
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern void SendCopyTextureToTexture(IntPtr self, IntPtr sel, IntPtr source, ulong slice, ulong level, MTLOrigin origin,
        MTLSize size, IntPtr destination, ulong dstSlice, ulong dstLevel, MTLOrigin dstOrigin);

    // copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toBuffer:destinationOffset:destinationBytesPerRow:destinationBytesPerImage:
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern void SendCopyTextureToBuffer(IntPtr self, IntPtr sel, IntPtr source, ulong slice, ulong level, MTLOrigin origin,
        MTLSize size, IntPtr buffer, ulong offset, ulong bytesPerRow, ulong bytesPerImage);

    static readonly Dictionary<string, IntPtr> Selectors = new();
    static readonly Dictionary<string, IntPtr> Classes = new();

    public static IntPtr Sel(string name)
    {
        lock (Selectors)
        {
            if (Selectors.TryGetValue(name, out var s)) return s;
            fixed (byte* p = Utf8(name)) s = sel_registerName(p);
            Selectors[name] = s;
            return s;
        }
    }

    public static IntPtr Class(string name)
    {
        lock (Classes)
        {
            if (Classes.TryGetValue(name, out var c)) return c;
            fixed (byte* p = Utf8(name)) c = objc_getClass(p);
            if (c == IntPtr.Zero) throw new InvalidOperationException($"Objective-C class '{name}' not found.");
            Classes[name] = c;
            return c;
        }
    }

    /// <summary>加载系统框架，使其中的 Objective-C 类对 objc_getClass 可见。失败时返回 false。</summary>
    public static bool LoadFramework(string path)
    {
        fixed (byte* b = Utf8(path))
            return dlopen(b, 1 | 8 /* RTLD_LAZY | RTLD_GLOBAL */) != IntPtr.Zero;
    }

    /// <summary>[[cls alloc] init]，返回 +1 引用。</summary>
    public static IntPtr New(string cls) => Send(Send(Class(cls), Sel("alloc")), Sel("init"));

    public static IntPtr NewString(string s)
    {
        fixed (char* p = s)
            return Send(Send(Class("NSString"), Sel("alloc")), Sel("initWithCharacters:length:"), p, (ulong)s.Length);
    }

    public static string? GetString(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero) return null;
        var utf8 = (byte*)Send(nsString, Sel("UTF8String"));
        if (utf8 == null) return null;
        int len = 0;
        while (utf8[len] != 0) len++;
        return Encoding.UTF8.GetString(utf8, len);
    }

    /// <summary>NSError 的 localizedDescription；没有错误时返回 null。</summary>
    public static string? ErrorDescription(IntPtr error) =>
        error == IntPtr.Zero ? null : GetString(Send(error, Sel("localizedDescription")));

    public static IntPtr Retain(IntPtr obj) => obj == IntPtr.Zero ? obj : Send(obj, Sel("retain"));

    public static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero) SendVoid(obj, Sel("release"));
    }

    public static void SetLabel(IntPtr obj, string? label)
    {
        if (label is null || obj == IntPtr.Zero) return;
        var s = NewString(label);
        SendVoid(obj, Sel("setLabel:"), s);
        Release(s);
    }

    static byte[] Utf8(string s)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, 0, s.Length, bytes, 0);
        return bytes;
    }
}

/// <summary>在作用域内推入一个自动释放池（返回自动释放对象的消息需要它，否则对象会累积到线程结束）。</summary>
internal readonly struct AutoreleasePool : IDisposable
{
    readonly IntPtr _pool;
    AutoreleasePool(IntPtr pool) => _pool = pool;
    public static AutoreleasePool Push() => new(ObjC.objc_autoreleasePoolPush());
    public void Dispose() => ObjC.objc_autoreleasePoolPop(_pool);
}

/// <summary>Metal 枚举值（MTLPixelFormat 等）。</summary>
internal static class Mtl
{
    public const ulong PixelFormatR8Unorm = 10;
    public const ulong PixelFormatRgba8Unorm = 70;
    public const ulong PixelFormatBgra8Unorm = 80;

    public const ulong TextureUsageShaderRead = 1;
    public const ulong TextureUsageRenderTarget = 4;

    public const ulong StorageModeShared = 0;
    public const ulong StorageModePrivate = 2;

    /// <summary>MTLResourceStorageModeShared | MTLResourceCPUCacheModeWriteCombined。</summary>
    public const ulong ResourceUploadOptions = 1;
    /// <summary>MTLResourceStorageModeShared（默认缓存模式，CPU 读取用）。</summary>
    public const ulong ResourceReadbackOptions = 0;

    public const ulong LoadActionDontCare = 0, LoadActionLoad = 1, LoadActionClear = 2;
    public const ulong StoreActionStore = 1;

    public const ulong BlendFactorZero = 0, BlendFactorOne = 1, BlendFactorOneMinusSourceAlpha = 5;

    public const ulong PrimitiveTypeTriangleStrip = 4;

    public const ulong SamplerFilterNearest = 0, SamplerFilterLinear = 1;
    public const ulong SamplerAddressClampToEdge = 0;

    public const ulong CommandBufferStatusCompleted = 4, CommandBufferStatusError = 5;

    public static ulong PixelFormat(GpuTextureFormat f) => f switch
    {
        GpuTextureFormat.Rgba8Unorm => PixelFormatRgba8Unorm,
        GpuTextureFormat.Bgra8Unorm => PixelFormatBgra8Unorm,
        _ => PixelFormatR8Unorm,
    };
}
