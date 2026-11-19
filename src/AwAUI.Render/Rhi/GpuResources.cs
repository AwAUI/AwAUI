using System;

namespace AwAUI.Render.Rhi;

/// <summary>纹理像素格式。全部为 8 位无符号归一化、非 sRGB 编码（与 design/13 的 sRGB 编码空间混合一致）。</summary>
public enum GpuTextureFormat : byte
{
    /// <summary>内存顺序 R, G, B, A。离屏目标、图层与渐变查找表使用。</summary>
    Rgba8Unorm,
    /// <summary>内存顺序 B, G, R, A。多数交换链的原生格式。</summary>
    Bgra8Unorm,
    /// <summary>单通道 8 位（覆盖率掩码、字形图集）。着色器读到的是 (r, 0, 0, 1)。</summary>
    R8Unorm,
}

[Flags]
public enum GpuTextureUsage : byte
{
    None = 0,
    /// <summary>可在着色器中读取。</summary>
    Sampled = 1,
    /// <summary>可作为渲染通道的颜色附件。</summary>
    RenderTarget = 2,
}

/// <summary>纹理描述。</summary>
public readonly struct GpuTextureDesc
{
    public GpuTextureDesc(int width, int height, GpuTextureFormat format, GpuTextureUsage usage, string? label = null)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        Width = width;
        Height = height;
        Format = format;
        Usage = usage;
        Label = label;
    }

    public int Width { get; }
    public int Height { get; }
    public GpuTextureFormat Format { get; }
    public GpuTextureUsage Usage { get; }

    /// <summary>调试标签（GPU 调试工具中可见）。</summary>
    public string? Label { get; }
}

/// <summary>采样器：一律钳制到边缘。</summary>
public enum GpuSamplerFilter : byte
{
    Nearest,
    Linear,
}

/// <summary>
/// 固定管线。每种图形 API 为每个值提供手写的着色器（design/03 D10），实例数据格式由 RHI 统一规定。
/// </summary>
public enum GpuPipelineKind : byte
{
    /// <summary>2D 四边形通用管线：实例格式见 <see cref="GpuQuadInstance"/>，常量见 <see cref="GpuQuadUniforms"/>。</summary>
    Quad,
}

/// <summary>固定功能混合状态。片元着色器输出预乘颜色。</summary>
public enum GpuBlendState : byte
{
    /// <summary>直接写入（不混合）。</summary>
    Replace,
    /// <summary>预乘 alpha 的 SrcOver：dst = src + dst·(1 − src.a)。</summary>
    PremultipliedOver,
}

/// <summary>渲染通道开始时如何处理附件已有内容。</summary>
public enum GpuLoadAction : byte
{
    /// <summary>保留已有内容。</summary>
    Load,
    /// <summary>以清屏色填满。</summary>
    Clear,
    /// <summary>内容未定义（调用方会覆盖全部像素）。</summary>
    DontCare,
}

/// <summary>GPU 资源的基类。Dispose 只释放本方的引用：仍被已提交命令使用的资源由图形 API 保证在执行完毕前有效。</summary>
public abstract class GpuResource : IDisposable
{
    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        Release();
    }

    protected abstract void Release();
}

/// <summary>二维纹理。</summary>
public abstract class GpuTexture : GpuResource
{
    protected GpuTexture(int width, int height, GpuTextureFormat format, GpuTextureUsage usage)
    {
        Width = width;
        Height = height;
        Format = format;
        Usage = usage;
    }

    public int Width { get; }
    public int Height { get; }
    public PixelSize Size => new(Width, Height);
    public GpuTextureFormat Format { get; }
    public GpuTextureUsage Usage { get; }

    public static int BytesPerPixel(GpuTextureFormat format) => format == GpuTextureFormat.R8Unorm ? 1 : 4;
}

/// <summary>GPU 缓冲（目前只由设备的上传环创建）。</summary>
public abstract class GpuBuffer : GpuResource
{
    protected GpuBuffer(int length) => Length = length;
    public int Length { get; }
}

/// <summary>缓冲中的一段（例如一帧的实例数据）。只在分配它的命令列表执行完毕之前有效。</summary>
public readonly struct GpuBufferRange
{
    public GpuBufferRange(GpuBuffer buffer, int offset, int length)
    {
        Buffer = buffer;
        Offset = offset;
        Length = length;
    }

    public GpuBuffer Buffer { get; }
    public int Offset { get; }
    public int Length { get; }
}

/// <summary>
/// 交换链：窗口表面的一组可轮换的图像。每帧 <see cref="AcquireNextTexture"/> → 渲染 → <see cref="GpuCommandList.Present"/> → 提交。
/// 取得的纹理只在本帧有效，内容在取得时未定义（每帧都要完整重画）。
/// </summary>
public abstract class GpuSwapchain : GpuResource
{
    public abstract PixelSize Size { get; }
    public abstract GpuTextureFormat Format { get; }

    /// <summary>改变图像尺寸（窗口大小或缩放变化后）。</summary>
    public abstract void Resize(PixelSize size);

    /// <summary>取得下一张可绘制的图像；表面不可用（尺寸为 0、窗口不可见等）时返回 null，调用方跳过本帧。</summary>
    public abstract GpuTexture? AcquireNextTexture();
}
