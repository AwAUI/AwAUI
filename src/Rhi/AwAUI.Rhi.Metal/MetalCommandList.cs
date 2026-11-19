using System;
using System.Collections.Generic;
using AwAUI.Render.Rhi;
using AwAUI.Rhi.Metal.Interop;
using static AwAUI.Rhi.Metal.Interop.ObjC;

namespace AwAUI.Rhi.Metal;

/// <summary>
/// 一个 MTLCommandBuffer。上传数据写入设备上传环的共享缓冲块，纹理更新编码为 blit（按命令顺序生效）。
/// 同一时间最多一个打开的编码器：blit 编码器在连续的上传 / 拷贝之间复用，开始渲染通道或提交时结束。
/// </summary>
internal sealed unsafe class MetalCommandList : GpuCommandList
{
    readonly MetalDevice _device;
    readonly List<UploadChunk> _chunks = new();
    IntPtr _commandBuffer;
    IntPtr _blit;
    MetalRenderPass? _pass;
    MetalSwapchain? _present;
    bool _done;

    public MetalCommandList(MetalDevice device)
    {
        _device = device;
        using (AutoreleasePool.Push())
            _commandBuffer = Retain(Send(device.Queue, Sel("commandBuffer")));
        if (_commandBuffer == IntPtr.Zero) throw new GpuException("Could not create a Metal command buffer.");
    }

    public override GpuBufferRange Upload(ReadOnlySpan<byte> data, int alignment = 256)
    {
        ThrowIfDone();
        if (alignment < 16 || (alignment & (alignment - 1)) != 0) throw new ArgumentOutOfRangeException(nameof(alignment));
        var (chunk, offset) = Allocate(Math.Max(1, data.Length), alignment);
        data.CopyTo(new Span<byte>((byte*)chunk.Contents + offset, data.Length));
        return new GpuBufferRange(chunk.Wrapper, offset, data.Length);
    }

    (UploadChunk Chunk, int Offset) Allocate(int size, int alignment)
    {
        if (_chunks.Count > 0)
        {
            var last = _chunks[_chunks.Count - 1];
            int o = MetalDevice.AlignUp(last.Used, alignment);
            if (o + size <= last.Size)
            {
                last.Used = o + size;
                return (last, o);
            }
        }
        var chunk = _device.RentChunk(size);
        chunk.Used = size;
        _chunks.Add(chunk);
        return (chunk, 0);
    }

    public override void UploadTexture(GpuTexture texture, PixelRect region, ReadOnlySpan<byte> data, int rowBytes)
    {
        ThrowIfDone();
        ThrowIfInPass();
        var t = (MetalTexture)texture;
        if (region.IsEmpty) return;
        if (region.X < 0 || region.Y < 0 || region.Right > t.Width || region.Bottom > t.Height)
            throw new ArgumentOutOfRangeException(nameof(region));
        int bpp = GpuTexture.BytesPerPixel(t.Format);
        int tight = region.Width * bpp;
        if (rowBytes < tight) throw new ArgumentOutOfRangeException(nameof(rowBytes));
        long needed = (long)rowBytes * (region.Height - 1) + tight;
        if (data.Length < needed) throw new ArgumentException("Not enough pixel data for the region.", nameof(data));

        // 按紧密行距复制进上传环（行距对齐到 4 字节）
        int pitch = MetalDevice.AlignUp(tight, 4);
        var (chunk, offset) = Allocate(pitch * region.Height, 256);
        byte* dst = (byte*)chunk.Contents + offset;
        for (int y = 0; y < region.Height; y++)
            data.Slice(y * rowBytes, tight).CopyTo(new Span<byte>(dst + (long)y * pitch, tight));

        var blit = Blit();
        SendCopyBufferToTexture(blit, Sel("copyFromBuffer:sourceOffset:sourceBytesPerRow:sourceBytesPerImage:sourceSize:toTexture:destinationSlice:destinationLevel:destinationOrigin:"),
            chunk.Buffer, (ulong)offset, (ulong)pitch, (ulong)(pitch * region.Height),
            new MTLSize(region.Width, region.Height), t.Handle, 0, 0, new MTLOrigin(region.X, region.Y));
    }

    public override void CopyTexture(GpuTexture source, PixelRect sourceRect, GpuTexture destination, int destinationX, int destinationY)
    {
        ThrowIfDone();
        ThrowIfInPass();
        var s = (MetalTexture)source;
        var d = (MetalTexture)destination;
        if (s.Format != d.Format) throw new ArgumentException("Texture formats must match.", nameof(destination));
        if (sourceRect.IsEmpty) return;
        if (sourceRect.X < 0 || sourceRect.Y < 0 || sourceRect.Right > s.Width || sourceRect.Bottom > s.Height
            || destinationX < 0 || destinationY < 0 || destinationX + sourceRect.Width > d.Width || destinationY + sourceRect.Height > d.Height)
            throw new ArgumentOutOfRangeException(nameof(sourceRect));
        var blit = Blit();
        SendCopyTextureToTexture(blit, Sel("copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toTexture:destinationSlice:destinationLevel:destinationOrigin:"),
            s.Handle, 0, 0, new MTLOrigin(sourceRect.X, sourceRect.Y), new MTLSize(sourceRect.Width, sourceRect.Height),
            d.Handle, 0, 0, new MTLOrigin(destinationX, destinationY));
    }

    public override GpuReadback ReadTexture(GpuTexture texture, PixelRect rect)
    {
        ThrowIfDone();
        ThrowIfInPass();
        var t = (MetalTexture)texture;
        rect = rect.Intersect(new PixelRect(0, 0, t.Width, t.Height));
        var readback = new MetalReadback(_device, rect.Size, t.Format);
        if (rect.IsEmpty) return readback;
        var blit = Blit();
        SendCopyTextureToBuffer(blit, Sel("copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toBuffer:destinationOffset:destinationBytesPerRow:destinationBytesPerImage:"),
            t.Handle, 0, 0, new MTLOrigin(rect.X, rect.Y), new MTLSize(rect.Width, rect.Height),
            readback.Buffer, 0, (ulong)readback.RowBytes, (ulong)(readback.RowBytes * rect.Height));
        readback.Attach(this);
        return readback;
    }

    internal MetalSubmission? Submission { get; private set; }

    public override GpuRenderPass BeginRenderPass(in GpuRenderPassDesc desc)
    {
        ThrowIfDone();
        ThrowIfInPass();
        EndBlit();
        var target = (MetalTexture)desc.Target;
        if ((target.Usage & GpuTextureUsage.RenderTarget) == 0) throw new ArgumentException("The texture is not a render target.", nameof(desc));
        IntPtr encoder;
        using (AutoreleasePool.Push())
        {
            var rp = Send(Class("MTLRenderPassDescriptor"), Sel("renderPassDescriptor"));
            var a0 = Send(Send(rp, Sel("colorAttachments")), Sel("objectAtIndexedSubscript:"), 0UL);
            SendVoid(a0, Sel("setTexture:"), target.Handle);
            SendVoid(a0, Sel("setLoadAction:"), desc.Load switch
            {
                GpuLoadAction.Clear => Mtl.LoadActionClear,
                GpuLoadAction.DontCare => Mtl.LoadActionDontCare,
                _ => Mtl.LoadActionLoad,
            });
            SendVoid(a0, Sel("setStoreAction:"), Mtl.StoreActionStore);
            if (desc.Load == GpuLoadAction.Clear)
            {
                var c = desc.ClearColor;
                float a = Clamp01(c.A);
                SendClearColor(a0, Sel("setClearColor:"), new MTLClearColor
                {
                    Red = Clamp01(c.R) * a, Green = Clamp01(c.G) * a, Blue = Clamp01(c.B) * a, Alpha = a,
                });
            }
            encoder = Retain(Send(_commandBuffer, Sel("renderCommandEncoderWithDescriptor:"), rp));
        }
        if (encoder == IntPtr.Zero) throw new GpuException("Could not create a Metal render command encoder.");
        _pass = new MetalRenderPass(this, _device, encoder, target);
        return _pass;
    }

    static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

    internal void PassEnded(MetalRenderPass pass)
    {
        if (_pass == pass) _pass = null;
    }

    public override void Present(GpuSwapchain swapchain)
    {
        ThrowIfDone();
        _present = (MetalSwapchain)swapchain;
    }

    public override GpuSubmission Submit()
    {
        ThrowIfDone();
        if (_pass != null) throw new InvalidOperationException("End the render pass before submitting.");
        if (_present is { CapturePresentedFrames: true } capture && capture.CurrentTexture is { } presented)
            capture.SetCapture(ReadTexture(presented, new PixelRect(0, 0, presented.Width, presented.Height)));
        EndBlit();
        IntPtr drawable = _present?.TakeDrawable() ?? IntPtr.Zero;
        // presentsWithTransaction：提交并等待调度后在本线程呈现，新帧随当前 CA 事务上屏（见 MetalSwapchain 说明）
        bool withTransaction = drawable != IntPtr.Zero && _present!.PresentsWithTransaction;
        if (drawable != IntPtr.Zero && !withTransaction) SendVoid(_commandBuffer, Sel("presentDrawable:"), drawable);
        SendVoid(_commandBuffer, Sel("commit"));
        if (withTransaction)
        {
            SendVoid(_commandBuffer, Sel("waitUntilScheduled"));
            SendVoid(drawable, Sel("present"));
        }
        Release(drawable);
        var submission = new MetalSubmission(_commandBuffer, _chunks.ToArray());
        _commandBuffer = IntPtr.Zero;
        _chunks.Clear();
        _done = true;
        Submission = submission;
        _device.Track(submission);
        return submission;
    }

    IntPtr Blit()
    {
        if (_blit != IntPtr.Zero) return _blit;
        using (AutoreleasePool.Push())
            _blit = Retain(Send(_commandBuffer, Sel("blitCommandEncoder")));
        if (_blit == IntPtr.Zero) throw new GpuException("Could not create a Metal blit encoder.");
        return _blit;
    }

    void EndBlit()
    {
        if (_blit == IntPtr.Zero) return;
        SendVoid(_blit, Sel("endEncoding"));
        Release(_blit);
        _blit = IntPtr.Zero;
    }

    void ThrowIfDone()
    {
        if (_done) throw new InvalidOperationException("The command list has already been submitted or disposed.");
    }

    void ThrowIfInPass()
    {
        if (_pass != null) throw new InvalidOperationException("This operation is not allowed while a render pass is open.");
    }

    public override void Dispose()
    {
        if (_done) return;
        _done = true;
        _pass?.Abandon();
        _pass = null;
        EndBlit();
        // 未提交的命令缓冲直接丢弃；上传块没有被 GPU 使用，立即归还
        Release(_commandBuffer);
        _commandBuffer = IntPtr.Zero;
        foreach (var c in _chunks) { c.Used = 0; _device.ReturnChunkIfUnused(c); }
        _chunks.Clear();
    }
}

internal sealed unsafe class MetalRenderPass : GpuRenderPass
{
    readonly MetalCommandList _owner;
    readonly MetalDevice _device;
    readonly MetalTexture _target;
    IntPtr _encoder;

    public MetalRenderPass(MetalCommandList owner, MetalDevice device, IntPtr encoder, MetalTexture target)
    {
        _owner = owner;
        _device = device;
        _encoder = encoder;
        _target = target;
    }

    public override GpuTexture Target => _target;

    IntPtr Encoder => _encoder != IntPtr.Zero ? _encoder : throw new InvalidOperationException("The render pass has ended.");

    public override void SetPipeline(GpuPipelineKind kind, GpuBlendState blend) =>
        SendVoid(Encoder, Sel("setRenderPipelineState:"), _device.Pipeline(kind, blend, _target.Format));

    public override void SetScissor(PixelRect rect)
    {
        rect = rect.Intersect(new PixelRect(0, 0, _target.Width, _target.Height));
        // Metal 不接受空的裁剪矩形：用 1×1 并在调用方跳过绘制
        if (rect.IsEmpty) rect = new PixelRect(0, 0, 1, 1);
        SendScissor(Encoder, Sel("setScissorRect:"), new MTLScissorRect
        {
            X = (ulong)rect.X, Y = (ulong)rect.Y, Width = (ulong)rect.Width, Height = (ulong)rect.Height,
        });
    }

    public override void SetTexture(int slot, GpuTexture texture, GpuSamplerFilter filter = GpuSamplerFilter.Nearest)
    {
        var t = (MetalTexture)texture;
        SendVoid(Encoder, Sel("setFragmentTexture:atIndex:"), t.Handle, (ulong)slot);
        SendVoid(Encoder, Sel("setFragmentSamplerState:atIndex:"), _device.Sampler(filter), (ulong)slot);
    }

    public override void SetBuffer(int slot, in GpuBufferRange range)
    {
        var b = (MetalBuffer)range.Buffer;
        SendVoid(Encoder, Sel("setVertexBuffer:offset:atIndex:"), b.Handle, (ulong)range.Offset, (ulong)slot);
        SendVoid(Encoder, Sel("setFragmentBuffer:offset:atIndex:"), b.Handle, (ulong)range.Offset, (ulong)slot);
    }

    public override void SetUniforms(int slot, ReadOnlySpan<byte> data)
    {
        if (data.Length > 4096) throw new ArgumentOutOfRangeException(nameof(data), "Uniform data is limited to 4 KB.");
        fixed (byte* p = data)
        {
            SendVoid(Encoder, Sel("setVertexBytes:length:atIndex:"), p, (ulong)data.Length, (ulong)slot);
            SendVoid(Encoder, Sel("setFragmentBytes:length:atIndex:"), p, (ulong)data.Length, (ulong)slot);
        }
    }

    public override void DrawInstanced(int vertexCount, int instanceCount, int firstInstance)
    {
        if (vertexCount <= 0 || instanceCount <= 0) return;
        SendVoid(Encoder, Sel("drawPrimitives:vertexStart:vertexCount:instanceCount:baseInstance:"),
            Mtl.PrimitiveTypeTriangleStrip, 0UL, (ulong)vertexCount, (ulong)instanceCount, (ulong)firstInstance);
    }

    public override void End()
    {
        if (_encoder == IntPtr.Zero) return;
        SendVoid(_encoder, Sel("endEncoding"));
        Release(_encoder);
        _encoder = IntPtr.Zero;
        _owner.PassEnded(this);
    }

    internal void Abandon() => End();
}

internal sealed class MetalSubmission : GpuSubmission
{
    IntPtr _commandBuffer;
    UploadChunk[] _chunks;
    TimeSpan? _gpuTime;

    public MetalSubmission(IntPtr commandBuffer, UploadChunk[] chunks)
    {
        _commandBuffer = commandBuffer;
        _chunks = chunks;
    }

    public string? ErrorMessage { get; private set; }
    public bool Failed => ErrorMessage is not null;

    public override bool IsCompleted
    {
        get
        {
            if (_commandBuffer == IntPtr.Zero) return true;
            ulong status = SendULong(_commandBuffer, Sel("status"));
            if (status < Mtl.CommandBufferStatusCompleted) return false;
            Finish(status);
            return true;
        }
    }

    public override TimeSpan? GpuTime => _gpuTime;

    public override void Wait()
    {
        WaitNoThrow();
        if (ErrorMessage is not null) throw new GpuException("GPU command buffer failed: " + ErrorMessage);
    }

    internal void WaitNoThrow()
    {
        if (_commandBuffer == IntPtr.Zero) return;
        SendVoid(_commandBuffer, Sel("waitUntilCompleted"));
        Finish(SendULong(_commandBuffer, Sel("status")));
    }

    void Finish(ulong status)
    {
        if (_commandBuffer == IntPtr.Zero) return;
        if (status == Mtl.CommandBufferStatusError)
        {
            using (AutoreleasePool.Push())
                ErrorMessage = ErrorDescription(Send(_commandBuffer, Sel("error"))) ?? "unknown error";
        }
        else
        {
            double start = SendDouble(_commandBuffer, Sel("GPUStartTime"));
            double end = SendDouble(_commandBuffer, Sel("GPUEndTime"));
            if (end > start && start > 0) _gpuTime = TimeSpan.FromTicks((long)((end - start) * TimeSpan.TicksPerSecond));
        }
        Release(_commandBuffer);
        _commandBuffer = IntPtr.Zero;
    }

    internal UploadChunk[] TakeChunks()
    {
        var c = _chunks;
        _chunks = Array.Empty<UploadChunk>();
        foreach (var chunk in c) chunk.Used = 0;
        return c;
    }
}

internal sealed unsafe class MetalReadback : GpuReadback
{
    MetalCommandList? _owner;

    public MetalReadback(MetalDevice device, PixelSize size, GpuTextureFormat format) : base(size, format)
    {
        Buffer = size.IsEmpty ? IntPtr.Zero : device.CreateReadbackBuffer(RowBytes * size.Height);
    }

    public IntPtr Buffer { get; private set; }

    internal void Attach(MetalCommandList owner) => _owner = owner;

    public override void CopyTo(Span<byte> destination, int destinationRowBytes)
    {
        if (Size.IsEmpty) return;
        if (Buffer == IntPtr.Zero) throw new ObjectDisposedException(nameof(GpuReadback));
        var submission = _owner?.Submission ?? throw new InvalidOperationException("Submit the command list before reading back.");
        submission.Wait();
        if (destinationRowBytes < RowBytes) throw new ArgumentOutOfRangeException(nameof(destinationRowBytes));
        if (destination.Length < (long)destinationRowBytes * (Size.Height - 1) + RowBytes)
            throw new ArgumentException("Destination is too small.", nameof(destination));
        var src = (byte*)Send(Buffer, Sel("contents"));
        for (int y = 0; y < Size.Height; y++)
            new ReadOnlySpan<byte>(src + (long)y * RowBytes, RowBytes).CopyTo(destination.Slice(y * destinationRowBytes, RowBytes));
    }

    public override void Dispose()
    {
        Release(Buffer);
        Buffer = IntPtr.Zero;
    }
}
