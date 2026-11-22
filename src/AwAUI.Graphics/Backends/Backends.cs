using System;
using System.Collections.Generic;

namespace AwAUI.Graphics;

/// <summary>像素格式。全部为 8 位每通道、预乘 alpha。</summary>
public enum PixelFormat : byte
{
    /// <summary>内存顺序 R, G, B, A。</summary>
    Rgba8888,
    /// <summary>内存顺序 B, G, R, A（Win32 DIB、多数 Linux 帧缓冲）。</summary>
    Bgra8888,
}

[Flags]
public enum BackendCapabilities
{
    None = 0,
    Gpu = 1 << 0,
    RetainedLayers = 1 << 1,
    BlurFilter = 1 << 2,
    BackdropFilter = 1 << 3,
    SubpixelText = 1 << 4,
    ColorGlyphs = 1 << 5,
    WideGamut = 1 << 6,
    AdvancedBlendModes = 1 << 7,
}

/// <summary>
/// 帧间复用已有像素的位块搬移（design/18「滚动：位块搬移已有像素 + 重画新露出的条带」）。
/// <see cref="Source"/> 是设备像素坐标下的源区域，目标区域是同一矩形平移 (<see cref="Dx"/>, <see cref="Dy"/>) 之后的结果；
/// 重叠的复制必须按 memmove 语义处理。渲染后端在光栅化 <see cref="RenderOptions.Damage"/> 之前执行搬移。
/// </summary>
public readonly struct FramebufferShift
{
    public FramebufferShift(PixelRect source, int dx, int dy)
    {
        Source = source;
        Dx = dx;
        Dy = dy;
    }

    /// <summary>源区域（设备像素），必须已经在目标位图之内。</summary>
    public PixelRect Source { get; }

    /// <summary>内容在设备像素上的水平位移（正 = 向右）。</summary>
    public int Dx { get; }

    /// <summary>内容在设备像素上的垂直位移（正 = 向下）。</summary>
    public int Dy { get; }

    /// <summary>没有可搬移的内容。</summary>
    public bool IsEmpty => Source.IsEmpty || (Dx == 0 && Dy == 0);
}

/// <summary>一次渲染的参数。</summary>
public readonly struct RenderOptions
{
    public RenderOptions(Color clearColor, PixelRect? damage = null, FramebufferShift? shift = null, IReadOnlyList<PixelRect>? damageRects = null)
    {
        ClearColor = clearColor;
        Damage = damage;
        Shift = shift;
        DamageRects = damageRects;
    }

    /// <summary>绘制前用此颜色清空目标（受损区域内）。</summary>
    public Color ClearColor { get; }

    /// <summary>只需重画的设备像素区域；为空表示整个目标。</summary>
    public PixelRect? Damage { get; }

    /// <summary>
    /// 受损区域的精确分解（设备像素，均在 <see cref="Damage"/> 之内）。纯滚动帧的损伤是「新露出的条带 + 滚动条条带」，
    /// 它们的包围盒是整个视口，所以支持多矩形的后端应按这些矩形分别重画；不支持的后端退回按 <see cref="Damage"/> 重画。
    /// </summary>
    public IReadOnlyList<PixelRect>? DamageRects { get; }

    /// <summary>
    /// 光栅化之前先把已有像素按此位移搬移（纯滚动帧的快路径）。只在 <see cref="Damage"/> 不为空时生效：
    /// 目的区域由搬移得到，其余受损区域（新露出的条带、滚动条）从 DisplayList 重画。
    /// 后端不支持复用像素时忽略它（结果仍是完整重画，因为 <see cref="Damage"/> 之外的像素保持不变）。
    /// </summary>
    public FramebufferShift? Shift { get; }
}

/// <summary>
/// 平台层与渲染后端之间的唯一契约：一块可以被绘制的原生表面（窗口、canvas、离屏位图）。
/// 后端通过 <see cref="GetFeature{T}"/> 查询自己能使用的形态。
/// </summary>
public abstract class NativeSurface
{
    public abstract PixelSize Size { get; }
    public abstract double Scale { get; }
    public virtual T? GetFeature<T>() where T : class => this as T;
}

/// <summary>可锁定为内存像素缓冲的表面（软件呈现）。</summary>
/// <remarks>
/// 契约：两次 <see cref="Lock"/> 之间缓冲保留上一次写入的内容（尺寸或缩放变化时除外，此时内容未定义），
/// 软件后端因此可以只光栅化损伤区域（<c>RenderOptions.Damage</c>，UI 宿主的局部光栅化依赖这一点）。
/// 不能保证这一点的实现应在每次锁定后视内容为未定义，并让宿主关闭局部光栅化。
/// </remarks>
public interface IFramebufferSurface
{
    ILockedFramebuffer Lock();
}

/// <summary>
/// 提供 CAMetalLayer 的原生表面（macOS / iOS 的平台层实现；Metal 设备据此创建交换链）。
/// 与 <see cref="IFramebufferSurface"/> 一样是表面能力接口（design/02），平台层因此不必引用 Metal 实现。
/// </summary>
public interface IMetalLayerSurface
{
    /// <summary>CAMetalLayer* 指针。层的生命周期由平台层管理，至少要长于交换链。</summary>
    IntPtr MetalLayer { get; }
}

/// <summary>
/// 提供 <c>ANativeWindow*</c> 的 Android 表面（<c>SurfaceView</c> 的 holder；EGL 据此创建窗口表面）。
/// 表面能力接口（design/02、design/24），平台层因此不必引用 GLES 实现。
/// </summary>
public interface IAndroidWindowSurface
{
    /// <summary><c>ANativeWindow*</c> 指针；表面销毁或尚未创建时为 <see cref="IntPtr.Zero"/>。</summary>
    IntPtr ANativeWindow { get; }
}

/// <summary>
/// 提供 <c>wl_egl_window*</c> 的 Wayland 表面（EGL 据此创建窗口表面）。
/// 表面能力接口（design/02、design/24），平台层因此不必引用 GLES 实现。
/// </summary>
public interface IWlEglSurface
{
    /// <summary><c>wl_display*</c> 指针（平台连接）。</summary>
    IntPtr WlDisplay { get; }

    /// <summary><c>wl_surface*</c> 指针；表面销毁后为 <see cref="IntPtr.Zero"/>。</summary>
    IntPtr WlSurface { get; }

    /// <summary><c>wl_egl_window*</c> 指针；尚未创建时为 <see cref="IntPtr.Zero"/>。</summary>
    IntPtr WlEglWindow { get; }
}

/// <summary>锁定期间可写的像素缓冲；Dispose 时提交显示。</summary>
public interface ILockedFramebuffer : IDisposable
{
    IntPtr Address { get; }
    PixelSize Size { get; }
    int RowBytes { get; }
    PixelFormat Format { get; }
}

/// <summary>
/// 渲染后端（design/02 的接缝②）。用抽象类而不是接口：发布后仍可追加带默认实现的成员（design/11）。
/// </summary>
public abstract class RenderBackend : IDisposable
{
    public abstract string Name { get; }
    public abstract BackendCapabilities Capabilities { get; }

    /// <summary>为原生表面创建渲染目标。表面不提供所需能力时抛出 <see cref="NotSupportedException"/>。</summary>
    public abstract RenderTarget CreateRenderTarget(NativeSurface surface);

    public abstract RenderTarget CreateOffscreenTarget(PixelSize size, double scale);

    /// <summary>设备丢失后调用：丢弃全部设备资源，之后可从逻辑资源重建。</summary>
    public virtual void InvalidateDeviceResources() { }

    /// <summary>后端发生能力降级（不支持的效果被忽略或近似）时触发，供诊断使用。</summary>
    public event Action<string>? Degraded;

    protected void ReportDegradation(string message) => Degraded?.Invoke(message);

    public virtual T? GetFeature<T>() where T : class => null;

    public virtual void Dispose() { }
}

/// <summary>渲染目标：把 DisplayList 画到一块表面上。</summary>
public abstract class RenderTarget : IDisposable
{
    public abstract PixelSize PixelSize { get; }
    public abstract double Scale { get; }

    /// <summary>窗口尺寸或缩放变化后调用（离屏目标可直接改变尺寸）。</summary>
    public abstract void Resize(PixelSize size, double scale);

    /// <summary>
    /// 渲染一帧。DisplayList 使用逻辑像素坐标，目标按 <see cref="Scale"/> 映射到设备像素。
    /// 层合成由上层的合成器负责（design/18），后端只需按命令绘制。
    /// </summary>
    public abstract void Render(DisplayList content, in RenderOptions options);

    /// <summary>
    /// 最近一次 <see cref="Render"/> 画出的帧是否已交给表面呈现。为 false 表示该帧被丢弃，表面上仍是更早的内容：
    /// 例如交换链在超时内没有给出可绘制纹理（窗口被遮挡、最小化、窗口服务器繁忙）。
    /// <para>
    /// 契约：每次 <see cref="Render"/> 返回时更新（抛出异常时不更新）；第一次渲染之前为 true。为 false 时调用方应在下一次机会
    /// （下一个节拍 / vsync，不要立即循环重试：取纹理本身可能已阻塞了一段时间）重新渲染，并把这一帧的损伤区域并入下一帧
    /// （交换链目标总是整帧重画，损伤区域被忽略）。丢帧不是错误，不经 <see cref="RenderBackend.Degraded"/> 上报。
    /// </para>
    /// 默认实现恒为 true：软件目标同步写入帧缓冲或位图，不会丢帧；离屏目标同样如此。
    /// </summary>
    public virtual bool LastFramePresented => true;

    /// <summary>回读像素（截图、黄金图像测试）。</summary>
    public abstract void ReadPixels(PixelRect rect, Span<byte> destination, PixelFormat format);

    public virtual void Dispose() { }
}
