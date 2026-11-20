using System;
using System.Numerics;
using System.Runtime.InteropServices;
using AwAUI.Render.Gpu.Atlas;
using AwAUI.Render.Rhi;

namespace AwAUI.Render.Gpu.Frame;

/// <summary>
/// 把 <see cref="FrameGraph"/> 编码成 RHI 命令：先上传（图集脏区、查找表、整帧实例数组一次），再按操作顺序编码主目标。
/// 渲染通道在第一次绘制时才打开；图层（<see cref="FrameOpKind.RenderLayer"/>）与目标拷贝（<see cref="FrameOpKind.DstCopy"/>）
/// 会结束已打开的通道，之后以 Load 重新打开。图层纹理在编码图层时租用、在它的合成绘制编码之后归还：
/// 同一队列上的命令按序执行，归还后的复用总是发生在合成读取之后。冗余的管线、纹理与常量绑定被跳过。
/// </summary>
internal sealed class FrameEncoder : IDisposable
{
    readonly GpuDevice _device;
    readonly TexturePool _pool;
    GpuTexture? _placeholder;
    GpuTexture? _dstRgba, _dstBgra;
    GpuCommandList _commands = null!;
    GpuBufferRange _instances;
    GpuBufferRange _clipShapes;
    long _frame;

    public FrameEncoder(GpuDevice device, TexturePool pool)
    {
        _device = device;
        _pool = pool;
    }

    public int DrawCalls { get; private set; }
    public int RenderPasses { get; private set; }
    public int DstCopies { get; private set; }
    public long UploadBytes { get; private set; }

    /// <summary>1×1 透明纹理：管线读取的槽位都必须绑定。</summary>
    GpuTexture Placeholder()
    {
        if (_placeholder is not null) return _placeholder;
        _placeholder = _device.CreateTexture(new GpuTextureDesc(1, 1, GpuTextureFormat.Rgba8Unorm, GpuTextureUsage.Sampled, "AwAUI placeholder"));
        Span<byte> zero = stackalloc byte[4];
        zero.Clear();
        _commands.UploadTexture(_placeholder, new PixelRect(0, 0, 1, 1), zero, 4);
        return _placeholder;
    }

    public void Encode(GpuCommandList commands, FrameGraph graph, GpuTexture mainTexture, GpuLoadAction mainLoad, Color clear,
        MaskAtlas atlas, GradientLuts luts, long frame, ImageTextures? images = null)
    {
        DrawCalls = RenderPasses = DstCopies = 0;
        _commands = commands;
        _mainTexture = mainTexture;
        _frame = frame;
        long uploaded = 0;
        Placeholder();
        graph.Main.Texture = mainTexture;
        PrepareDstTextures(graph, mainTexture.Format);
        CountUses(graph);

        atlas.Upload(commands, ref uploaded);
        luts.Upload(commands, ref uploaded);
        images?.Upload(commands, ref uploaded);
        var instances = MemoryMarshal.AsBytes(graph.Instances);
        _instances = instances.Length > 0 ? commands.Upload(instances) : default;
        uploaded += instances.Length;
        // 着色器只在实例引用时读裁剪形状数组，但槽位必须绑定：没有形状时绑定实例缓冲
        var clips = MemoryMarshal.AsBytes(graph.ClipShapes);
        _clipShapes = clips.Length > 0 ? commands.Upload(clips) : _instances;
        uploaded += clips.Length;
        UploadBytes = uploaded;

        try
        {
            EncodeTarget(graph.Main, mainLoad, clear);
        }
        finally
        {
            _commands = null!;
            _instances = _clipShapes = default;
        }
    }

    /// <summary>
    /// 统计每个离屏目标被多少个绘制操作读取（槽位 0 与 1）：最后一次读取编码之后纹理归还给纹理池。
    /// 普通图层只被它的合成读取一次；滤镜的中间图像可能被读取两次（投影的原图）。
    /// </summary>
    static void CountUses(FrameGraph graph)
    {
        graph.Main.Uses = 0;
        graph.Main.Rendered = true;
        foreach (var l in graph.Layers)
        {
            l.Uses = 0;
            l.Rendered = false;
        }
        static void Count(FrameTarget t)
        {
            foreach (var op in t.Ops)
            {
                if (op.Kind != FrameOpKind.Draw) continue;
                if (op.Coverage is FrameTarget a) a.Uses++;
                if (op.Lut is FrameTarget b && !ReferenceEquals(b, op.Coverage)) b.Uses++;
            }
        }
        Count(graph.Main);
        foreach (var l in graph.Layers) Count(l);
    }

    void Release(FrameTarget t)
    {
        if (--t.Uses > 0 || t.Texture is null || ReferenceEquals(t.Texture, _mainTexture)) return;
        _pool.Return(t.Texture);
        t.Texture = null;
    }

    GpuTexture? _mainTexture;

    /// <summary>为每种目标格式准备够大的目标拷贝纹理（同一帧内顺序复用同一张）。</summary>
    void PrepareDstTextures(FrameGraph graph, GpuTextureFormat mainFormat)
    {
        int rw = 0, rh = 0, bw = 0, bh = 0;
        void Scan(FrameTarget t, GpuTextureFormat f)
        {
            foreach (var op in t.Ops)
            {
                if (op.Kind != FrameOpKind.DstCopy) continue;
                if (f == GpuTextureFormat.Bgra8Unorm) { bw = Math.Max(bw, op.CopyRect.Width); bh = Math.Max(bh, op.CopyRect.Height); }
                else { rw = Math.Max(rw, op.CopyRect.Width); rh = Math.Max(rh, op.CopyRect.Height); }
            }
        }
        Scan(graph.Main, mainFormat);
        foreach (var l in graph.Layers) Scan(l, GpuTextureFormat.Rgba8Unorm);
        _dstRgba = Ensure(_dstRgba, rw, rh, GpuTextureFormat.Rgba8Unorm);
        _dstBgra = Ensure(_dstBgra, bw, bh, GpuTextureFormat.Bgra8Unorm);
    }

    GpuTexture? Ensure(GpuTexture? current, int w, int h, GpuTextureFormat format)
    {
        if (w == 0 || h == 0) return current;
        if (current is not null && current.Width >= w && current.Height >= h) return current;
        int nw = Math.Max(w, current?.Width ?? 0), nh = Math.Max(h, current?.Height ?? 0);
        current?.Dispose();
        return _device.CreateTexture(new GpuTextureDesc(nw, nh, format, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled, "AwAUI dst copy"));
    }

    static GpuTexture? Resolve(object? source) => source switch
    {
        AtlasPage p => p.Texture,
        FrameTarget t => t.Texture,
        GradientLuts.LutPage l => l.Texture,
        ImageTextures.Entry i => i.Texture,
        _ => null,
    };

    /// <summary>一个目标的编码状态：渲染通道按需打开，记录已绑定的状态以跳过冗余调用。</summary>
    sealed class TargetState
    {
        public GpuRenderPass? Pass;
        public GpuLoadAction NextLoad;
        public Color Clear;
        public bool HasPipeline, HasUniforms;
        public GpuBlendState Blend;
        public GpuTexture? Coverage, Lut, Clip, Dst;
        public Vector4 DstOrigin;
    }

    void EncodeTarget(FrameTarget target, GpuLoadAction load, Color clear)
    {
        var texture = target.Texture!;
        var dst = texture.Format == GpuTextureFormat.Bgra8Unorm ? _dstBgra : _dstRgba;
        var placeholder = _placeholder!;
        var uniforms = new GpuQuadUniforms
        {
            Target = new Vector4(target.Rect.X, target.Rect.Y, 2f / texture.Width, 2f / texture.Height),
        };
        var st = new TargetState { NextLoad = load, Clear = clear };

        foreach (var op in target.Ops)
        {
            switch (op.Kind)
            {
                case FrameOpKind.RenderLayer:
                {
                    var layer = op.Layer!;
                    // 滤镜的中间图像可能被两个后续步骤引用：只渲染一次
                    if (layer.Rendered) continue;
                    EndPass(st);
                    layer.Rendered = true;
                    layer.Texture = _pool.Rent(layer.Rect.Width, layer.Rect.Height, GpuTextureFormat.Rgba8Unorm, _frame);
                    EncodeTarget(layer, GpuLoadAction.Clear, default);
                    continue;
                }
                case FrameOpKind.CopyToLayer:
                {
                    if (st.Pass is null && st.NextLoad == GpuLoadAction.Clear) st.Pass = BeginPass(texture, st);
                    EndPass(st);
                    var layer = op.Layer!;
                    layer.Rendered = true;
                    var r = op.CopyRect;
                    layer.Texture = _pool.Rent(r.Width, r.Height, texture.Format, _frame);
                    _commands.CopyTexture(texture, new PixelRect(r.X - target.Rect.X, r.Y - target.Rect.Y, r.Width, r.Height), layer.Texture, 0, 0);
                    if (layer.Uses <= 0)
                    {
                        _pool.Return(layer.Texture);
                        layer.Texture = null;
                    }
                    continue;
                }
                case FrameOpKind.DstCopy:
                {
                    // 还没清屏的目标要先清，否则拷贝到的是旧内容
                    if (st.Pass is null && st.NextLoad == GpuLoadAction.Clear) st.Pass = BeginPass(texture, st);
                    EndPass(st);
                    var src = new PixelRect(op.CopyRect.X - target.Rect.X, op.CopyRect.Y - target.Rect.Y, op.CopyRect.Width, op.CopyRect.Height);
                    _commands.CopyTexture(texture, src, dst!, 0, 0);
                    DstCopies++;
                    continue;
                }
            }

            var pass = st.Pass ??= BeginPass(texture, st);
            if (st.Blend != op.Blend || !st.HasPipeline)
            {
                pass.SetPipeline(GpuPipelineKind.Quad, op.Blend);
                st.Blend = op.Blend;
                st.HasPipeline = true;
            }
            Bind(pass, GpuQuadUniforms.CoverageTextureSlot, Resolve(op.Coverage) ?? placeholder, ref st.Coverage);
            Bind(pass, GpuQuadUniforms.GradientTextureSlot, Resolve(op.Lut) ?? placeholder, ref st.Lut);
            Bind(pass, GpuQuadUniforms.ClipTextureSlot, Resolve(op.Clip) ?? placeholder, ref st.Clip);
            Bind(pass, GpuQuadUniforms.DstTextureSlot, op.CopyOp is not null ? dst! : placeholder, ref st.Dst);
            var dstOrigin = op.CopyOp is { } copy ? new Vector4(copy.CopyRect.X, copy.CopyRect.Y, 0, 0) : default;
            if (!st.HasUniforms || st.DstOrigin != dstOrigin)
            {
                uniforms.Dst = dstOrigin;
                SetUniforms(pass, uniforms);
                st.DstOrigin = dstOrigin;
                st.HasUniforms = true;
            }
            pass.DrawInstanced(GpuQuadUniforms.VertexCount, op.Count, op.First);
            DrawCalls++;

            // 图层的合成（或滤镜的最后一次读取）已经编码：纹理可以给后面的图层复用
            if (op.Coverage is FrameTarget composited)
            {
                Release(composited);
                if (composited.Texture is null) st.Coverage = null;
            }
            if (op.Lut is FrameTarget second && !ReferenceEquals(second, op.Coverage))
            {
                Release(second);
                if (second.Texture is null) st.Lut = null;
            }
        }
        // 没有任何绘制的目标也要执行清屏
        if (st.Pass is null && st.NextLoad == GpuLoadAction.Clear) st.Pass = BeginPass(texture, st);
        EndPass(st);
    }

    GpuRenderPass BeginPass(GpuTexture texture, TargetState st)
    {
        var pass = _commands.BeginRenderPass(new GpuRenderPassDesc(texture, st.NextLoad, st.Clear));
        RenderPasses++;
        if (_instances.Buffer is not null) pass.SetBuffer(GpuQuadUniforms.InstanceSlot, _instances);
        if (_clipShapes.Buffer is not null) pass.SetBuffer(GpuQuadUniforms.ClipShapeSlot, _clipShapes);
        st.NextLoad = GpuLoadAction.Load;
        st.HasPipeline = st.HasUniforms = false;
        st.Coverage = st.Lut = st.Clip = st.Dst = null;
        return pass;
    }

    static void EndPass(TargetState st)
    {
        if (st.Pass is null) return;
        st.Pass.End();
        st.Pass = null;
    }

    static unsafe void SetUniforms(GpuRenderPass pass, GpuQuadUniforms uniforms) =>
        pass.SetUniforms(GpuQuadUniforms.Slot, new ReadOnlySpan<byte>(&uniforms, sizeof(GpuQuadUniforms)));

    static void Bind(GpuRenderPass pass, int slot, GpuTexture texture, ref GpuTexture? current)
    {
        if (ReferenceEquals(current, texture)) return;
        pass.SetTexture(slot, texture);
        current = texture;
    }

    public void Dispose()
    {
        _placeholder?.Dispose();
        _dstRgba?.Dispose();
        _dstBgra?.Dispose();
        _placeholder = _dstRgba = _dstBgra = null;
    }
}
