using System;
using AwAUI.Graphics;

namespace AwAUI.Render.Rhi;

/// <summary>
/// GPU 设备（design/02 的接缝③、design/03「RHI 保持极小」）。只覆盖 2D 渲染器实际需要的子集：
/// 纹理（RGBA8 / BGRA8 / A8）、帧内上传环、少量以枚举标识的固定管线、渲染通道、交换链呈现与回读。
/// 不是通用的图形 API 抽象：没有着色器对象、顶点布局或描述符集，每种图形 API 自带固定管线的着色器。
/// <para>
/// 线程模型：一个设备同一时间只被一个线程使用（渲染线程）；命令列表按创建顺序提交。
/// 用抽象类而不是接口，以便日后追加带默认实现的成员（design/11）。
/// </para>
/// </summary>
public abstract class GpuDevice : IDisposable
{
    /// <summary>设备名（诊断用），例如 "Apple M2"。</summary>
    public abstract string Name { get; }

    /// <summary>图形 API 名（诊断用），例如 "Metal"。</summary>
    public abstract string Api { get; }

    public abstract GpuCapabilities Capabilities { get; }

    /// <summary>创建纹理。内容未定义，渲染目标在第一次以 <see cref="GpuLoadAction.Clear"/> 开始的通道里初始化。</summary>
    public abstract GpuTexture CreateTexture(in GpuTextureDesc desc);

    /// <summary>
    /// 为原生表面创建交换链。表面不提供本 API 能用的形态（例如 Metal 需要 CAMetalLayer）时抛出 <see cref="NotSupportedException"/>。
    /// </summary>
    public abstract GpuSwapchain CreateSwapchain(NativeSurface surface);

    /// <summary>
    /// 开始录制一组命令（通常一帧一个）。在 <see cref="GpuCommandList.Submit"/> 之前，它分配的上传数据一直有效；
    /// 设备在内部限制同时在 GPU 上执行的命令列表数量，必要时这里会等待最早的一个完成。
    /// </summary>
    public abstract GpuCommandList BeginCommands();

    /// <summary>等待已提交的全部命令执行完毕。</summary>
    public abstract void WaitIdle();

    /// <summary>设备丢失（驱动重置、外接显卡拔出等）。之后应丢弃全部设备资源并重建设备。</summary>
    public event Action? DeviceLost;

    protected void OnDeviceLost() => DeviceLost?.Invoke();

    public abstract void Dispose();
}

/// <summary>设备能力与限制。</summary>
public sealed class GpuCapabilities
{
    public GpuCapabilities(int maxTextureSize, bool supportsGpuTimestamps)
    {
        MaxTextureSize = maxTextureSize;
        SupportsGpuTimestamps = supportsGpuTimestamps;
    }

    /// <summary>二维纹理的最大边长（像素）。</summary>
    public int MaxTextureSize { get; }

    /// <summary><see cref="GpuSubmission.GpuTime"/> 是否可用。</summary>
    public bool SupportsGpuTimestamps { get; }
}
