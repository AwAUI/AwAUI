using System;
using AwAUI.Graphics;
using AwAUI.Render.Rhi;
using AwAUI.Rhi.Metal.Interop;
using static AwAUI.Rhi.Metal.Interop.ObjC;

namespace AwAUI.Rhi.Metal;

/// <summary>
/// 基于 CAMetalLayer 的交换链。层由平台窗口创建并挂在 NSView 上（AwAUI.MacOS 把它作为子层铺满视图的后备层，
/// 并负责 <c>frame</c> 与 <c>contentsScale</c>）；这里只配置设备、像素格式（BGRA8Unorm）与可绘制尺寸，每帧取 nextDrawable。
/// <c>framebufferOnly</c> 关闭：读目标的混合模式需要从可绘制纹理拷贝像素。
/// <para>
/// 呈现方式跟随层的 <c>presentsWithTransaction</c>（由平台层设置，例如 AwAUI.MacOS 在拖动缩放期间打开）：关闭时命令缓冲
/// <c>presentDrawable:</c>（GPU 完成后异步上屏）；打开时提交后等待命令缓冲被调度，再在调用线程上 <c>[drawable present]</c>，
/// 让新帧与当前 Core Animation 事务（例如窗口的新尺寸）一起上屏。
/// </para>
/// </summary>
public sealed class MetalSwapchain : GpuSwapchain
{
    readonly MetalDevice _device;
    readonly IntPtr _layer;
    PixelSize _size;
    IntPtr _drawable;
    MetalTexture? _texture;

    internal MetalSwapchain(MetalDevice device, IntPtr layer, PixelSize size)
    {
        _device = device;
        _layer = Retain(layer);
        SendVoid(_layer, Sel("setDevice:"), device.Handle);
        SendVoid(_layer, Sel("setPixelFormat:"), Mtl.PixelFormatBgra8Unorm);
        SendBool(_layer, Sel("setFramebufferOnly:"), 0);
        SendVoid(_layer, Sel("setMaximumDrawableCount:"), 3UL);
        Resize(size);
    }

    /// <summary>CAMetalLayer* 指针。</summary>
    public IntPtr Layer => _layer;

    public override PixelSize Size => _size;
    public override GpuTextureFormat Format => GpuTextureFormat.Bgra8Unorm;

    /// <summary>是否与显示器同步呈现（CAMetalLayer.displaySyncEnabled，默认开）。关闭后用于测量不受刷新率限制的帧时间。</summary>
    public bool DisplaySync
    {
        get => _displaySync;
        set
        {
            _displaySync = value;
            SendBool(_layer, Sel("setDisplaySyncEnabled:"), value ? (byte)1 : (byte)0);
        }
    }

    bool _displaySync = true;

    /// <summary>
    /// 最近一次 <c>nextDrawable</c> 的耗时（诊断）。三张可绘制纹理都还在使用（GPU 或显示器尚未释放）时它会阻塞，
    /// 这段时间是等待而不是 CPU 工作，测量每帧 CPU 耗时时应扣除。
    /// </summary>
    public TimeSpan LastAcquireTime { get; private set; }

    /// <summary>层当前是否要求随 Core Animation 事务呈现（每帧读取，平台层可随时切换）。</summary>
    internal bool PresentsWithTransaction => SendByte(_layer, Sel("presentsWithTransaction")) != 0;

    /// <summary>
    /// 诊断：为 true 时每次呈现前把可绘制纹理复制到回读缓冲（每帧多一次整帧 blit），<see cref="ReadPresentedFrame"/> 读回最近呈现的那一帧。
    /// 用于在无法截屏的环境里自检窗口里实际呈现的像素。
    /// </summary>
    public bool CapturePresentedFrames { get; set; }

    GpuReadback? _capture;

    /// <summary>本帧取得、尚未呈现的可绘制纹理。</summary>
    internal MetalTexture? CurrentTexture => _texture;

    internal void SetCapture(GpuReadback readback)
    {
        _capture?.Dispose();
        _capture = readback;
    }

    /// <summary>
    /// 读回最近一次呈现的可绘制纹理（RGBA8，预乘）；需要先打开 <see cref="CapturePresentedFrames"/>，还没有捕获到帧时返回 null。
    /// 会等待那一帧在 GPU 上执行完毕。
    /// </summary>
    public Bitmap? ReadPresentedFrame()
    {
        var capture = _capture;
        if (capture is null || capture.Size.IsEmpty) return null;
        using var bgra = new Bitmap(capture.Size, PixelFormat.Bgra8888);
        unsafe
        {
            capture.CopyTo(new Span<byte>((void*)bgra.Address, bgra.RowBytes * bgra.Height), bgra.RowBytes);
        }
        var rgba = new Bitmap(capture.Size);
        bgra.CopyTo(rgba);
        return rgba;
    }

    public override void Resize(PixelSize size)
    {
        if (size != _size && _drawable != IntPtr.Zero)
        {
            // 取得后没有呈现的可绘制对象（上一帧在编码中途失败）还是旧尺寸：丢弃，下一帧按新的可绘制尺寸重新取得，
            // 否则 AcquireNextTexture 会复用它，新尺寸的内容被画进旧尺寸的纹理
            _texture?.Dispose();
            _texture = null;
            ObjC.Release(_drawable);
            _drawable = IntPtr.Zero;
        }
        _size = size;
        if (!size.IsEmpty) SendSize(_layer, Sel("setDrawableSize:"), new CGSize(size.Width, size.Height));
    }

    public override GpuTexture? AcquireNextTexture()
    {
        if (_size.IsEmpty) return null;
        if (_drawable != IntPtr.Zero)
        {
            // 上一张取得后没有呈现：直接复用
            return _texture;
        }
        IntPtr drawable, texture;
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        using (AutoreleasePool.Push())
        {
            drawable = Retain(Send(_layer, Sel("nextDrawable")));
            LastAcquireTime = TimeSpan.FromTicks((long)((System.Diagnostics.Stopwatch.GetTimestamp() - start) * (double)TimeSpan.TicksPerSecond / System.Diagnostics.Stopwatch.Frequency));
            texture = drawable == IntPtr.Zero ? IntPtr.Zero : Retain(Send(drawable, Sel("texture")));
        }
        if (drawable == IntPtr.Zero || texture == IntPtr.Zero)
        {
            ObjC.Release(drawable);
            return null;
        }
        _drawable = drawable;
        int w = (int)SendULong(texture, Sel("width")), h = (int)SendULong(texture, Sel("height"));
        _texture = new MetalTexture(texture, w, h, GpuTextureFormat.Bgra8Unorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled, owned: true);
        return _texture;
    }

    /// <summary>交出当前可绘制对象（+1 引用）给命令缓冲呈现；之后需要重新取得。</summary>
    internal IntPtr TakeDrawable()
    {
        var d = _drawable;
        _drawable = IntPtr.Zero;
        _texture?.Dispose();
        _texture = null;
        return d;
    }

    protected override void Release()
    {
        _capture?.Dispose();
        _capture = null;
        _texture?.Dispose();
        _texture = null;
        ObjC.Release(_drawable);
        _drawable = IntPtr.Zero;
        ObjC.Release(_layer);
        GC.KeepAlive(_device);
    }
}
