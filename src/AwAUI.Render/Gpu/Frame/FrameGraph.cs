using System;
using System.Collections.Generic;
using AwAUI.Render.Rhi;

namespace AwAUI.Render.Gpu.Frame;

/// <summary>
/// 一帧录制的结果：所有实例放在同一个数组里（一次上传），每个渲染目标（主目标或离屏图层）是一串按顺序执行的操作。
/// 图层在父目标的操作序列中以 <see cref="FrameOpKind.RenderLayer"/> 出现在它的合成之前：编码时就地渲染，合成后纹理立即归还，
/// 因此同时占用的离屏纹理数只取决于嵌套深度，而不是本帧的图层总数。父目标的渲染通道在第一次绘制时才打开，
/// 位于开头的图层不会打断它。
/// </summary>
internal sealed class FrameGraph
{
    GpuQuadInstance[] _instances = new GpuQuadInstance[1024];
    GpuClipShape[] _clips = new GpuClipShape[16];

    public FrameGraph()
    {
        Main = new FrameTarget(default, null);
    }

    public FrameTarget Main { get; private set; }

    public int InstanceCount { get; private set; }

    public ReadOnlySpan<GpuQuadInstance> Instances => new(_instances, 0, InstanceCount);

    public int ClipShapeCount { get; private set; }

    /// <summary>本帧的解析裁剪形状（实例以下标 + 1 引用）。</summary>
    public ReadOnlySpan<GpuClipShape> ClipShapes => new(_clips, 0, ClipShapeCount);

    public int AddClipShape(in GpuClipShape shape)
    {
        if (ClipShapeCount == _clips.Length) Array.Resize(ref _clips, _clips.Length * 2);
        _clips[ClipShapeCount] = shape;
        return ClipShapeCount++;
    }

    /// <summary>本帧所有图层目标（诊断用）。</summary>
    public List<FrameTarget> Layers { get; } = new();

    public void Reset(PixelRect mainRect)
    {
        InstanceCount = 0;
        ClipShapeCount = 0;
        Layers.Clear();
        Main = new FrameTarget(mainRect, null);
    }

    public int Add(in GpuQuadInstance instance)
    {
        if (InstanceCount == _instances.Length) Array.Resize(ref _instances, _instances.Length * 2);
        _instances[InstanceCount] = instance;
        return InstanceCount++;
    }

    public ref GpuQuadInstance At(int index) => ref _instances[index];
}

/// <summary>一个渲染目标：主目标（纹理在编码时给出）或离屏图层（编码时从纹理池租用，合成后归还）。</summary>
internal sealed class FrameTarget
{
    public FrameTarget(PixelRect rect, GpuTexture? texture)
    {
        Rect = rect;
        Texture = texture;
    }

    /// <summary>目标覆盖的设备像素区域；纹理左上角对应 Rect 的左上角。</summary>
    public PixelRect Rect { get; }

    public GpuTexture? Texture { get; set; }

    public List<FrameOp> Ops { get; } = new();

    /// <summary>编码时：还有多少个绘制操作要读取本目标的纹理（为 0 时归还给纹理池）。</summary>
    internal int Uses;

    /// <summary>纹理已经渲染（或拷贝）完成：再次出现的 <see cref="FrameOpKind.RenderLayer"/> 跳过它。</summary>
    internal bool Rendered;
}

internal enum FrameOpKind : byte
{
    /// <summary>一批连续的实例。</summary>
    Draw,
    /// <summary>把目标的 <see cref="FrameOp.CopyRect"/> 拷贝到目标拷贝纹理（读目标的混合模式需要），会打断渲染通道。</summary>
    DstCopy,
    /// <summary>渲染图层 <see cref="FrameOp.Layer"/> 的内容（在它的合成之前），会打断已打开的渲染通道。</summary>
    RenderLayer,
    /// <summary>
    /// 把当前目标的 <see cref="FrameOp.CopyRect"/> 拷贝到新租用的纹理，作为 <see cref="FrameOp.Layer"/> 的内容（背景滤镜的输入，格式与当前目标相同），
    /// 会打断渲染通道。
    /// </summary>
    CopyToLayer,
}

/// <summary>帧内的一个操作。</summary>
internal sealed class FrameOp
{
    public FrameOpKind Kind;
    public int First;
    public int Count;
    /// <summary>槽位 0 的纹理来源：<see cref="Atlas.AtlasPage"/>（掩码 / 字形）或 <see cref="FrameTarget"/>（图层）；null 时绑定占位纹理。</summary>
    public object? Coverage;
    /// <summary>槽位 1：<see cref="Atlas.GradientLuts.LutPage"/>、位图纹理（<see cref="Atlas.ImageTextures.Entry"/>）或滤镜的第二输入（<see cref="FrameTarget"/>）。</summary>
    public object? Lut;
    /// <summary>槽位 2：裁剪蒙版所在的 <see cref="Atlas.AtlasPage"/>。</summary>
    public object? Clip;
    public GpuBlendState Blend;
    /// <summary>DstCopy：要拷贝的设备像素区域（同组的实例加入时扩大）。</summary>
    public PixelRect CopyRect;
    /// <summary>读目标的绘制：它所依赖的 DstCopy（着色器常量取其区域左上角）。</summary>
    public FrameOp? CopyOp;
    /// <summary>RenderLayer：要渲染的图层。</summary>
    public FrameTarget? Layer;
}
