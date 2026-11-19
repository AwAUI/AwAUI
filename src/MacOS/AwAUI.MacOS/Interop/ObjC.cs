using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace AwAUI.MacOS.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct NSPoint { public double X, Y; public NSPoint(double x, double y) { X = x; Y = y; } }

[StructLayout(LayoutKind.Sequential)]
internal struct NSSize { public double Width, Height; public NSSize(double w, double h) { Width = w; Height = h; } }

[StructLayout(LayoutKind.Sequential)]
internal struct NSRect
{
    public NSPoint Origin;
    public NSSize Size;
    public NSRect(double x, double y, double w, double h) { Origin = new NSPoint(x, y); Size = new NSSize(w, h); }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NSRange { public ulong Location, Length; }

/// <summary>
/// Objective-C 运行时的裸互操作。所有签名只用 blittable 类型（design/11），
/// 同一份代码可用于 .NET Framework 之外的所有运行时并且 NativeAOT 友好。
/// objc_msgSend 按调用约定声明多个重载，入口点相同。
/// </summary>
internal static unsafe class ObjC
{
    const string Lib = "/usr/lib/libobjc.A.dylib";

    [DllImport(Lib)] static extern IntPtr objc_getClass(byte* name);
    [DllImport(Lib)] static extern IntPtr sel_registerName(byte* name);
    [DllImport(Lib)] static extern IntPtr objc_allocateClassPair(IntPtr superclass, byte* name, UIntPtr extraBytes);
    [DllImport(Lib)] static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(Lib)] static extern byte class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, byte* types);
    [DllImport(Lib)] static extern IntPtr objc_getProtocol(byte* name);
    [DllImport(Lib)] static extern byte class_addProtocol(IntPtr cls, IntPtr protocol);
    [DllImport(Lib)] public static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(Lib)] public static extern void objc_autoreleasePoolPop(IntPtr pool);

    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a, IntPtr b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a, IntPtr b, IntPtr c);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a, IntPtr b, IntPtr c, IntPtr d);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, long a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a, long b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendBool(IntPtr self, IntPtr sel, byte a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void Send(IntPtr self, IntPtr sel, IntPtr a, IntPtr b, byte c);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendRectBool(IntPtr self, IntPtr sel, NSRect a, byte b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendDoubleArg(IntPtr self, IntPtr sel, double a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void Send(IntPtr self, IntPtr sel, NSSize a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, NSRect rect);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, NSRect rect, ulong style, ulong backing, byte defer);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, NSRect rect, ulong options, IntPtr owner, IntPtr userInfo);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, char* chars, ulong length);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void Send(IntPtr self, IntPtr sel, char* buffer, NSRange range);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr self, IntPtr sel, IntPtr a, NSRange b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern byte SendByte(IntPtr self, IntPtr sel, IntPtr a, IntPtr b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(IntPtr self, IntPtr sel, IntPtr a, NSRange b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern void SendVoid(IntPtr self, IntPtr sel, IntPtr a, NSRange b, NSRange c);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern NSRange SendRange(IntPtr self, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern NSPoint SendPoint(IntPtr self, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern NSPoint SendPoint(IntPtr self, IntPtr sel, NSPoint p, IntPtr view);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern double SendDouble(IntPtr self, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern ulong SendULong(IntPtr self, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern byte SendByte(IntPtr self, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern byte SendByte(IntPtr self, IntPtr sel, IntPtr a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendEvent(IntPtr self, IntPtr sel, ulong type, NSPoint location, ulong flags, double timestamp,
        long windowNumber, IntPtr context, short subtype, long data1, long data2);

    // 返回大于 16 字节结构体的消息：arm64 走 objc_msgSend，x86_64 走 objc_msgSend_stret
    [DllImport(Lib, EntryPoint = "objc_msgSend")] static extern NSRect SendRectArm64(IntPtr self, IntPtr sel);
    [DllImport(Lib, EntryPoint = "objc_msgSend_stret")] static extern void SendRectX64(out NSRect result, IntPtr self, IntPtr sel);

    public static NSRect SendRect(IntPtr self, IntPtr sel)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64) return SendRectArm64(self, sel);
        SendRectX64(out var r, self, sel);
        return r;
    }

    [DllImport(Lib, EntryPoint = "objc_msgSend")] static extern NSRect SendRectArm64(IntPtr self, IntPtr sel, NSRect a, IntPtr b);
    [DllImport(Lib, EntryPoint = "objc_msgSend_stret")] static extern void SendRectX64(out NSRect result, IntPtr self, IntPtr sel, NSRect a, IntPtr b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] static extern NSRect SendRectArm64(IntPtr self, IntPtr sel, NSRect a);
    [DllImport(Lib, EntryPoint = "objc_msgSend_stret")] static extern void SendRectX64(out NSRect result, IntPtr self, IntPtr sel, NSRect a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] static extern NSRect SendRectArm64(IntPtr self, IntPtr sel, NSRange a, IntPtr b);
    [DllImport(Lib, EntryPoint = "objc_msgSend_stret")] static extern void SendRectX64(out NSRect result, IntPtr self, IntPtr sel, NSRange a, IntPtr b);

    /// <summary>-(NSRect)xxx:(NSRect)r yyy:(id)o（如 convertRect:toView:）。</summary>
    public static NSRect SendRect(IntPtr self, IntPtr sel, NSRect a, IntPtr b)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64) return SendRectArm64(self, sel, a, b);
        SendRectX64(out var r, self, sel, a, b);
        return r;
    }

    /// <summary>-(NSRect)xxx:(NSRect)r（如 convertRectToScreen:）。</summary>
    public static NSRect SendRect(IntPtr self, IntPtr sel, NSRect a)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64) return SendRectArm64(self, sel, a);
        SendRectX64(out var r, self, sel, a);
        return r;
    }

    /// <summary>-(NSRect)xxx:(NSRange)r yyy:(NSRangePointer)p（如 firstRectForCharacterRange:actualRange:，测试探针用）。</summary>
    public static NSRect SendRect(IntPtr self, IntPtr sel, NSRange a, IntPtr b)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64) return SendRectArm64(self, sel, a, b);
        SendRectX64(out var r, self, sel, a, b);
        return r;
    }

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

    /// <summary>查找类；不存在（所在框架未加载或系统版本太旧）时返回 <see cref="IntPtr.Zero"/>，不抛异常。</summary>
    public static IntPtr TryClass(string name)
    {
        lock (Classes)
        {
            if (Classes.TryGetValue(name, out var c)) return c;
            fixed (byte* p = Utf8(name)) c = objc_getClass(p);
            if (c != IntPtr.Zero) Classes[name] = c;
            return c;
        }
    }

    public static IntPtr Retain(IntPtr obj) => obj == IntPtr.Zero ? obj : Send(obj, Sel("retain"));

    /// <summary>在运行时注册一个新的 Objective-C 类。</summary>
    public static IntPtr DefineClass(string name, string superclass, IEnumerable<(string Selector, IntPtr Imp, string Types)> methods)
        => DefineClass(name, superclass, methods, null);

    /// <summary>注册新类并声明遵循的协议（协议不存在时忽略）。</summary>
    public static IntPtr DefineClass(string name, string superclass, IEnumerable<(string Selector, IntPtr Imp, string Types)> methods,
        IEnumerable<string>? protocols)
    {
        IntPtr cls;
        fixed (byte* n = Utf8(name)) cls = objc_allocateClassPair(Class(superclass), n, UIntPtr.Zero);
        if (cls == IntPtr.Zero)
        {
            // 同名类已存在（例如同一进程内重复初始化）
            return Class(name);
        }
        foreach (var (selector, imp, types) in methods)
            fixed (byte* t = Utf8(types)) class_addMethod(cls, Sel(selector), imp, t);
        if (protocols is not null)
        {
            foreach (var protocol in protocols)
            {
                IntPtr p;
                fixed (byte* pn = Utf8(protocol)) p = objc_getProtocol(pn);
                if (p != IntPtr.Zero) class_addProtocol(cls, p);
            }
        }
        objc_registerClassPair(cls);
        lock (Classes) Classes[name] = cls;
        return cls;
    }

    public static IntPtr NewString(string s)
    {
        fixed (char* p = s)
            return Send(Send(Class("NSString"), Sel("alloc")), Sel("initWithCharacters:length:"), p, (ulong)s.Length);
    }

    public static string? GetString(IntPtr nsString) => GetString(nsString, int.MaxValue);

    /// <summary>读取 NSString，最多 <paramref name="maxLength"/> 个 UTF-16 码元（不切开代理对）。</summary>
    public static string? GetString(IntPtr nsString, int maxLength)
    {
        if (nsString == IntPtr.Zero) return null;
        ulong full = SendULong(nsString, Sel("length"));
        int len = (int)Math.Min(full, (ulong)Math.Max(0, maxLength));
        if (len == 0) return string.Empty;
        var buffer = new char[len];
        fixed (char* p = buffer) Send(nsString, Sel("getCharacters:range:"), p, new NSRange { Location = 0, Length = (ulong)len });
        if ((ulong)len < full && char.IsHighSurrogate(buffer[len - 1])) len--;
        return new string(buffer, 0, len);
    }

    public static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero) Send(obj, Sel("release"));
    }

    static byte[] Utf8(string s)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, 0, s.Length, bytes, 0);
        return bytes;
    }
}

/// <summary>CoreGraphics 与 libSystem 的少量函数。</summary>
internal static unsafe class CG
{
    const string Lib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    const string LibSystem = "/usr/lib/libSystem.dylib";

    [DllImport(Lib)] public static extern IntPtr CGColorSpaceCreateWithName(IntPtr name);
    [DllImport(Lib)] public static extern IntPtr CGColorSpaceCreateDeviceRGB();
    [DllImport(Lib)] public static extern void CGColorSpaceRelease(IntPtr cs);
    [DllImport(Lib)] public static extern IntPtr CGDataProviderCreateWithData(IntPtr info, IntPtr data, UIntPtr size, IntPtr release);
    [DllImport(Lib)] public static extern void CGDataProviderRelease(IntPtr provider);
    [DllImport(Lib)]
    public static extern IntPtr CGImageCreate(UIntPtr width, UIntPtr height, UIntPtr bitsPerComponent, UIntPtr bitsPerPixel,
        UIntPtr bytesPerRow, IntPtr space, uint bitmapInfo, IntPtr provider, IntPtr decode, byte shouldInterpolate, int intent);
    [DllImport(Lib)] public static extern void CGImageRelease(IntPtr image);
    [DllImport(Lib)] public static extern void CGContextDrawImage(IntPtr ctx, NSRect rect, IntPtr image);
    [DllImport(Lib)] public static extern void CGContextSetInterpolationQuality(IntPtr ctx, int quality);
    [DllImport(Lib)] public static extern void CGContextSetBlendMode(IntPtr ctx, int mode);
    [DllImport(Lib)] public static extern IntPtr CGWindowListCreateImage(NSRect screenBounds, uint listOption, uint windowId, uint imageOption);
    [DllImport(Lib)] public static extern UIntPtr CGImageGetWidth(IntPtr image);
    [DllImport(Lib)] public static extern UIntPtr CGImageGetHeight(IntPtr image);
    [DllImport(Lib)]
    public static extern IntPtr CGBitmapContextCreate(IntPtr data, UIntPtr width, UIntPtr height, UIntPtr bitsPerComponent, UIntPtr bytesPerRow,
        IntPtr space, uint bitmapInfo);
    [DllImport(Lib)] public static extern void CGContextRelease(IntPtr ctx);

    [DllImport(LibSystem)] static extern IntPtr dlopen(byte* path, int mode);
    [DllImport(LibSystem)] static extern IntPtr dlsym(IntPtr handle, byte* symbol);

    public const uint AlphaPremultipliedLast = 1;
    public const int BlendModeCopy = 17;

    /// <summary>加载系统框架（例如 AppKit）。只有已加载框架中的 Objective-C 类才能被 objc_getClass 找到。</summary>
    public static void LoadFramework(string path)
    {
        var p = Encoding.UTF8.GetBytes(path + "\0");
        fixed (byte* b = p)
            if (dlopen(b, 1 | 8 /* RTLD_LAZY | RTLD_GLOBAL */) == IntPtr.Zero)
                throw new DllNotFoundException($"Could not load {path}.");
    }

    /// <summary>读取框架导出的常量（例如 kCGColorSpaceSRGB 是一个 CFStringRef 全局变量）。</summary>
    public static IntPtr GetConstant(string framework, string symbol)
    {
        var fw = Encoding.UTF8.GetBytes(framework + "\0");
        var sym = Encoding.UTF8.GetBytes(symbol + "\0");
        fixed (byte* f = fw)
        fixed (byte* s = sym)
        {
            var handle = dlopen(f, 1);
            if (handle == IntPtr.Zero) return IntPtr.Zero;
            var addr = dlsym(handle, s);
            return addr == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)addr;
        }
    }
}
