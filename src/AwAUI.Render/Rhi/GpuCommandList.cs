using System;

namespace AwAUI.Render.Rhi;

/// <summary>
/// 一组按顺序执行的 GPU 命令（通常一帧一个）。上传、拷贝与回读在渲染通道之外录制；
/// 同一时间最多有一个打开的渲染通道。命令列表在 <see cref="Submit"/> 之后不能再使用。
/// </summary>
public abstract class GpuCommandList : IDisposable
{
    /// <summary>
    /// 把数据复制进设备的上传环，返回在本命令列表执行完毕之前有效的缓冲区段（例如实例数据）。
    /// 偏移按 <paramref name="alignment"/> 对齐（2 的幂，至少 16）。
    /// </summary>
    public abstract GpuBufferRange Upload(ReadOnlySpan<byte> data, int alignment = 256);

    /// <summary>
    /// 更新纹理的一个区域。<paramref name="data"/> 从区域左上角像素开始，行距为 <paramref name="rowBytes"/> 字节。
    /// 按命令顺序生效：之前录制的绘制看到的是旧内容。不能在渲染通道打开时调用。
    /// </summary>
    public abstract void UploadTexture(GpuTexture texture, PixelRect region, ReadOnlySpan<byte> data, int rowBytes);

    /// <summary>纹理之间的像素拷贝。两者格式必须相同；不能在渲染通道打开时调用。</summary>
    public abstract void CopyTexture(GpuTexture source, PixelRect sourceRect, GpuTexture destination, int destinationX, int destinationY);

    /// <summary>开始一个渲染通道；返回的对象在 <see cref="GpuRenderPass.End"/> 之前有效。</summary>
    public abstract GpuRenderPass BeginRenderPass(in GpuRenderPassDesc desc);

    /// <summary>
    /// 录制一次纹理回读。数据在本命令列表执行完毕后可用（<see cref="GpuReadback.CopyTo"/> 会等待）。
    /// 不能在渲染通道打开时调用。
    /// </summary>
    public abstract GpuReadback ReadTexture(GpuTexture texture, PixelRect rect);

    /// <summary>在本命令列表执行完毕后呈现交换链最近一次取得的图像。</summary>
    public abstract void Present(GpuSwapchain swapchain);

    /// <summary>提交执行。之后本对象不可再用。</summary>
    public abstract GpuSubmission Submit();

    /// <summary>未提交就释放时丢弃已录制的命令。</summary>
    public abstract void Dispose();
}

/// <summary>渲染通道描述：单个颜色附件。</summary>
public readonly struct GpuRenderPassDesc
{
    public GpuRenderPassDesc(GpuTexture target, GpuLoadAction load, Color clearColor = default)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        Load = load;
        ClearColor = clearColor;
    }

    public GpuTexture Target { get; }
    public GpuLoadAction Load { get; }

    /// <summary>清屏色（非预乘，<see cref="GpuLoadAction.Clear"/> 时使用；实现负责预乘）。</summary>
    public Color ClearColor { get; }
}

/// <summary>
/// 渲染通道中的绘制命令。视口总是整个附件；三角形带的顶点由着色器按顶点序号生成，不需要顶点缓冲。
/// </summary>
public abstract class GpuRenderPass
{
    public abstract GpuTexture Target { get; }

    public abstract void SetPipeline(GpuPipelineKind kind, GpuBlendState blend);

    /// <summary>裁剪矩形（附件像素坐标）；默认整个附件。</summary>
    public abstract void SetScissor(PixelRect rect);

    /// <summary>绑定片元着色器的纹理槽位与采样器。管线读取的槽位都必须绑定。</summary>
    public abstract void SetTexture(int slot, GpuTexture texture, GpuSamplerFilter filter = GpuSamplerFilter.Nearest);

    /// <summary>绑定顶点与片元着色器共用的数据缓冲（例如实例数组）到槽位。</summary>
    public abstract void SetBuffer(int slot, in GpuBufferRange range);

    /// <summary>设置一小块常量数据（不超过 4 KB），顶点与片元着色器共用。</summary>
    public abstract void SetUniforms(int slot, ReadOnlySpan<byte> data);

    /// <summary>
    /// 实例化绘制：每个实例 <paramref name="vertexCount"/> 个顶点（三角形带），实例序号从 <paramref name="firstInstance"/> 开始。
    /// </summary>
    public abstract void DrawInstanced(int vertexCount, int instanceCount, int firstInstance);

    public abstract void End();
}

/// <summary>一次提交的句柄。</summary>
public abstract class GpuSubmission
{
    /// <summary>GPU 是否已执行完毕（成功或失败）。</summary>
    public abstract bool IsCompleted { get; }

    /// <summary>阻塞等待执行完毕。执行出错时抛出 <see cref="GpuException"/>。</summary>
    public abstract void Wait();

    /// <summary>GPU 上的执行时长；未完成或不支持时为 null。</summary>
    public abstract TimeSpan? GpuTime { get; }
}

/// <summary>一次纹理回读的结果。</summary>
public abstract class GpuReadback : IDisposable
{
    protected GpuReadback(PixelSize size, GpuTextureFormat format)
    {
        Size = size;
        Format = format;
    }

    public PixelSize Size { get; }
    public GpuTextureFormat Format { get; }
    public int RowBytes => Size.Width * GpuTexture.BytesPerPixel(Format);

    /// <summary>复制到目标（行距 <paramref name="destinationRowBytes"/>）。命令尚未执行完毕时会等待。</summary>
    public abstract void CopyTo(Span<byte> destination, int destinationRowBytes);

    public abstract void Dispose();
}

/// <summary>GPU 执行或资源创建失败。</summary>
public sealed class GpuException : Exception
{
    public GpuException(string message) : base(message) { }
}
