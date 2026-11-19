using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AwAUI.Graphics;
using AwAUI.Render.Rhi;
using AwAUI.Rhi.Metal.Interop;
using static AwAUI.Rhi.Metal.Interop.ObjC;

namespace AwAUI.Rhi.Metal;

/// <summary>
/// Metal 实现的 <see cref="GpuDevice"/>：系统默认设备、一个命令队列、运行时编译的 MSL 固定管线。
/// 只能在 macOS 上使用（iOS 将来复用同一实现）；用 <see cref="IsSupported"/> 或 <see cref="TryCreate"/> 探测。
/// </summary>
public sealed unsafe class MetalDevice : GpuDevice
{
    /// <summary>同时在 GPU 上执行的命令列表上限；超出时 <see cref="BeginCommands"/> 等待最早的一个。</summary>
    public const int MaxFramesInFlight = 3;

    const int UploadChunkSize = 4 << 20;

    /// <summary>
    /// 复用池中空闲上传块的总字节数上限。超出时释放最大的空闲块：一帧里的大上传（超大实例数组、比图集页还大的掩码）
    /// 不会让池子永久占着峰值时的内存。
    /// </summary>
    internal const long MaxFreeUploadBytes = 32L << 20;

    readonly IntPtr _device;
    readonly IntPtr _queue;
    readonly IntPtr _library;
    readonly IntPtr _vertexFunction;
    readonly IntPtr _fragmentFunction;
    readonly IntPtr[] _samplers = new IntPtr[2];
    readonly Dictionary<(GpuPipelineKind, GpuBlendState, GpuTextureFormat), IntPtr> _pipelines = new();
    readonly Queue<MetalSubmission> _inFlight = new();
    readonly List<UploadChunk> _freeChunks = new();
    long _freeChunkBytes;
    readonly string _name;
    bool _disposed;

    MetalDevice(IntPtr device)
    {
        _device = device;
        using (AutoreleasePool.Push())
            _name = GetString(Send(device, Sel("name"))) ?? "Metal";
        _queue = Send(device, Sel("newCommandQueue"));
        if (_queue == IntPtr.Zero) throw new GpuException("Could not create a Metal command queue.");

        using (AutoreleasePool.Push())
        {
            var source = NewString(MetalShaders.Source);
            var options = New("MTLCompileOptions");
            IntPtr error = IntPtr.Zero;
            _library = Send(device, Sel("newLibraryWithSource:options:error:"), source, options, (IntPtr)(&error));
            Release(options);
            Release(source);
            if (_library == IntPtr.Zero)
                throw new GpuException("Metal shader compilation failed: " + (ErrorDescription(error) ?? "unknown error"));
            _vertexFunction = NewFunction(MetalShaders.VertexFunction);
            _fragmentFunction = NewFunction(MetalShaders.FragmentFunction);
        }

        _samplers[(int)GpuSamplerFilter.Nearest] = CreateSampler(Mtl.SamplerFilterNearest);
        _samplers[(int)GpuSamplerFilter.Linear] = CreateSampler(Mtl.SamplerFilterLinear);
        Capabilities = new GpuCapabilities(maxTextureSize: 16384, supportsGpuTimestamps: true);
    }

    /// <summary>当前进程能否创建 Metal 设备（macOS 且有支持 Metal 的 GPU）。</summary>
    public static bool IsSupported => s_supported.Value;

    static readonly Lazy<bool> s_supported = new(() =>
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return false;
        try
        {
            LoadFrameworks();
            var d = MTLCreateSystemDefaultDevice();
            if (d == IntPtr.Zero) return false;
            Release(d);
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    });

    /// <summary>创建系统默认 Metal 设备。不支持时抛出 <see cref="PlatformNotSupportedException"/>，着色器编译失败时抛出 <see cref="GpuException"/>。</summary>
    public static MetalDevice Create()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            throw new PlatformNotSupportedException("Metal is only available on Apple platforms.");
        LoadFrameworks();
        var device = MTLCreateSystemDefaultDevice();
        if (device == IntPtr.Zero) throw new PlatformNotSupportedException("No Metal device is available.");
        try
        {
            return new MetalDevice(device);
        }
        catch
        {
            Release(device);
            throw;
        }
    }

    /// <summary>
    /// 命令行进程中，CoreGraphics 未加载时 MTLCreateSystemDefaultDevice 可能返回 nil；QuartzCore 提供 CAMetalLayer。
    /// </summary>
    static void LoadFrameworks()
    {
        LoadFramework(CoreGraphicsFramework);
        LoadFramework(MetalFramework);
        LoadFramework(QuartzCoreFramework);
    }

    /// <summary>尝试创建设备；不支持 Metal 时返回 null。</summary>
    public static MetalDevice? TryCreate() => IsSupported ? Create() : null;

    public override string Name => _name;
    public override string Api => "Metal";
    public override GpuCapabilities Capabilities { get; }

    /// <summary>id&lt;MTLDevice&gt; 指针（给需要直接调用 Metal 的代码，例如逃生舱 DrawCustom）。</summary>
    public IntPtr Handle => _device;

    internal IntPtr Queue => _queue;

    public override GpuTexture CreateTexture(in GpuTextureDesc desc)
    {
        ThrowIfDisposed();
        int max = Capabilities.MaxTextureSize;
        if (desc.Width > max || desc.Height > max)
            throw new ArgumentOutOfRangeException(nameof(desc), $"Texture size {desc.Width}x{desc.Height} exceeds the device limit {max}.");
        IntPtr texture;
        using (AutoreleasePool.Push())
        {
            var d = Send(Class("MTLTextureDescriptor"), Sel("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
                Mtl.PixelFormat(desc.Format), (ulong)desc.Width, (ulong)desc.Height, (byte)0);
            ulong usage = Mtl.TextureUsageShaderRead;
            if ((desc.Usage & GpuTextureUsage.RenderTarget) != 0) usage |= Mtl.TextureUsageRenderTarget;
            SendVoid(d, Sel("setUsage:"), usage);
            SendVoid(d, Sel("setStorageMode:"), Mtl.StorageModePrivate);
            texture = Send(_device, Sel("newTextureWithDescriptor:"), d);
        }
        if (texture == IntPtr.Zero) throw new GpuException($"Could not create a {desc.Width}x{desc.Height} {desc.Format} texture.");
        SetLabel(texture, desc.Label);
        return new MetalTexture(texture, desc.Width, desc.Height, desc.Format, desc.Usage, owned: true);
    }

    public override GpuSwapchain CreateSwapchain(NativeSurface surface)
    {
        if (surface is null) throw new ArgumentNullException(nameof(surface));
        var layer = surface.GetFeature<IMetalLayerSurface>()
                    ?? throw new NotSupportedException("The Metal device requires a surface that provides IMetalLayerSurface (CAMetalLayer).");
        return CreateSwapchain(layer.MetalLayer, surface.Size);
    }

    /// <summary>直接用 CAMetalLayer 指针创建交换链（平台窗口尚未提供 <see cref="IMetalLayerSurface"/> 时使用）。</summary>
    public MetalSwapchain CreateSwapchain(IntPtr metalLayer, PixelSize size)
    {
        ThrowIfDisposed();
        if (metalLayer == IntPtr.Zero) throw new ArgumentNullException(nameof(metalLayer));
        return new MetalSwapchain(this, metalLayer, size);
    }

    public override GpuCommandList BeginCommands()
    {
        ThrowIfDisposed();
        Recycle(block: false);
        while (_inFlight.Count >= MaxFramesInFlight) Recycle(block: true);
        return new MetalCommandList(this);
    }

    public override void WaitIdle()
    {
        while (_inFlight.Count > 0) Recycle(block: true);
    }

    /// <summary>回收已完成提交的上传块；<paramref name="block"/> 时至少等待最早的一个完成。</summary>
    void Recycle(bool block)
    {
        while (_inFlight.Count > 0)
        {
            var s = _inFlight.Peek();
            if (!s.IsCompleted)
            {
                if (!block) return;
                s.WaitNoThrow();
                block = false;
            }
            _inFlight.Dequeue();
            foreach (var chunk in s.TakeChunks()) ReturnChunk(chunk);
            if (s.Failed) OnDeviceLostIfNeeded(s);
        }
    }

    void OnDeviceLostIfNeeded(MetalSubmission s)
    {
        // 命令缓冲出错多半意味着 GPU 重置或设备移除：通知上层重建资源
        if (s.ErrorMessage is not null) OnDeviceLost();
    }

    internal void Track(MetalSubmission submission) => _inFlight.Enqueue(submission);

    /// <summary>
    /// 模拟设备丢失：触发 <see cref="GpuDevice.DeviceLost"/>，与命令缓冲出错时的路径相同（测试与诊断用，见 <see cref="MetalRenderBackend.SimulateDeviceLoss"/>）。
    /// 设备本身仍可用，只是订阅方会按丢失处理。
    /// </summary>
    internal void SimulateDeviceLoss() => OnDeviceLost();

    /// <summary>
    /// 租用至少 <paramref name="minSize"/> 字节的上传块：复用能装下的最小空闲块，没有时新建
    /// （默认 4 MB，更大的请求按需向上取整到 4 KB）。
    /// </summary>
    internal UploadChunk RentChunk(int minSize)
    {
        int best = -1;
        for (int i = 0; i < _freeChunks.Count; i++)
        {
            int size = _freeChunks[i].Size;
            if (size >= minSize && (best < 0 || size < _freeChunks[best].Size)) best = i;
        }
        if (best >= 0)
        {
            var c = _freeChunks[best];
            _freeChunks[best] = _freeChunks[_freeChunks.Count - 1];
            _freeChunks.RemoveAt(_freeChunks.Count - 1);
            _freeChunkBytes -= c.Size;
            return c;
        }
        int alloc = Math.Max(UploadChunkSize, AlignUp(minSize, 4096));
        var buffer = Send(_device, Sel("newBufferWithLength:options:"), (ulong)alloc, Mtl.ResourceUploadOptions);
        if (buffer == IntPtr.Zero) throw new GpuException($"Could not allocate a {alloc}-byte upload buffer.");
        UploadChunkCount++;
        UploadChunkBytes += alloc;
        return new UploadChunk(buffer, alloc);
    }

    /// <summary>归还没有被 GPU 使用过的上传块（命令列表未提交就被释放）。</summary>
    internal void ReturnChunkIfUnused(UploadChunk chunk) => ReturnChunk(chunk);

    /// <summary>把 GPU 已不再使用的上传块放回复用池；池中空闲字节超过 <see cref="MaxFreeUploadBytes"/> 时释放最大的块。</summary>
    void ReturnChunk(UploadChunk chunk)
    {
        chunk.Used = 0;
        _freeChunks.Add(chunk);
        _freeChunkBytes += chunk.Size;
        while (_freeChunkBytes > MaxFreeUploadBytes && _freeChunks.Count > 0)
        {
            int largest = 0;
            for (int i = 1; i < _freeChunks.Count; i++)
                if (_freeChunks[i].Size > _freeChunks[largest].Size) largest = i;
            var c = _freeChunks[largest];
            _freeChunks[largest] = _freeChunks[_freeChunks.Count - 1];
            _freeChunks.RemoveAt(_freeChunks.Count - 1);
            _freeChunkBytes -= c.Size;
            ReleaseChunk(c);
        }
    }

    void ReleaseChunk(UploadChunk chunk)
    {
        Release(chunk.Buffer);
        UploadChunkCount--;
        UploadChunkBytes -= chunk.Size;
    }

    /// <summary>当前存在的上传块数（在用 + 空闲，诊断与测试用）。</summary>
    internal int UploadChunkCount { get; private set; }

    /// <summary>当前存在的上传块总字节数（在用 + 空闲）。</summary>
    internal long UploadChunkBytes { get; private set; }

    internal IntPtr CreateReadbackBuffer(int size)
    {
        var buffer = Send(_device, Sel("newBufferWithLength:options:"), (ulong)Math.Max(16, size), Mtl.ResourceReadbackOptions);
        if (buffer == IntPtr.Zero) throw new GpuException($"Could not allocate a {size}-byte readback buffer.");
        return buffer;
    }

    internal static int AlignUp(int v, int a) => (v + a - 1) & ~(a - 1);

    internal IntPtr Sampler(GpuSamplerFilter f) => _samplers[(int)f];

    internal IntPtr Pipeline(GpuPipelineKind kind, GpuBlendState blend, GpuTextureFormat format)
    {
        if (_pipelines.TryGetValue((kind, blend, format), out var p)) return p;
        var desc = New("MTLRenderPipelineDescriptor");
        try
        {
            SendVoid(desc, Sel("setVertexFunction:"), _vertexFunction);
            SendVoid(desc, Sel("setFragmentFunction:"), _fragmentFunction);
            var attachments = Send(desc, Sel("colorAttachments"));
            var a0 = Send(attachments, Sel("objectAtIndexedSubscript:"), 0UL);
            SendVoid(a0, Sel("setPixelFormat:"), Mtl.PixelFormat(format));
            if (blend == GpuBlendState.PremultipliedOver)
            {
                SendBool(a0, Sel("setBlendingEnabled:"), 1);
                SendVoid(a0, Sel("setSourceRGBBlendFactor:"), Mtl.BlendFactorOne);
                SendVoid(a0, Sel("setSourceAlphaBlendFactor:"), Mtl.BlendFactorOne);
                SendVoid(a0, Sel("setDestinationRGBBlendFactor:"), Mtl.BlendFactorOneMinusSourceAlpha);
                SendVoid(a0, Sel("setDestinationAlphaBlendFactor:"), Mtl.BlendFactorOneMinusSourceAlpha);
            }
            IntPtr error = IntPtr.Zero;
            using (AutoreleasePool.Push())
            {
                p = Send(_device, Sel("newRenderPipelineStateWithDescriptor:error:"), desc, (IntPtr)(&error));
                if (p == IntPtr.Zero)
                    throw new GpuException($"Could not create the {kind} pipeline: " + (ErrorDescription(error) ?? "unknown error"));
            }
        }
        finally
        {
            Release(desc);
        }
        _pipelines[(kind, blend, format)] = p;
        return p;
    }

    IntPtr NewFunction(string name)
    {
        var s = NewString(name);
        var f = Send(_library, Sel("newFunctionWithName:"), s);
        Release(s);
        if (f == IntPtr.Zero) throw new GpuException($"Metal function '{name}' not found.");
        return f;
    }

    IntPtr CreateSampler(ulong filter)
    {
        var d = New("MTLSamplerDescriptor");
        SendVoid(d, Sel("setMinFilter:"), filter);
        SendVoid(d, Sel("setMagFilter:"), filter);
        SendVoid(d, Sel("setSAddressMode:"), Mtl.SamplerAddressClampToEdge);
        SendVoid(d, Sel("setTAddressMode:"), Mtl.SamplerAddressClampToEdge);
        var s = Send(_device, Sel("newSamplerStateWithDescriptor:"), d);
        Release(d);
        return s;
    }

    void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MetalDevice));
    }

    public override void Dispose()
    {
        if (_disposed) return;
        WaitIdle();
        _disposed = true;
        foreach (var p in _pipelines.Values) Release(p);
        _pipelines.Clear();
        foreach (var c in _freeChunks) ReleaseChunk(c);
        _freeChunks.Clear();
        _freeChunkBytes = 0;
        foreach (var s in _samplers) Release(s);
        Release(_vertexFunction);
        Release(_fragmentFunction);
        Release(_library);
        Release(_queue);
        Release(_device);
    }
}

/// <summary>上传环中的一块共享内存缓冲。</summary>
internal sealed class UploadChunk
{
    public UploadChunk(IntPtr buffer, int size)
    {
        Buffer = buffer;
        Size = size;
        Contents = ObjC.Send(buffer, ObjC.Sel("contents"));
        Wrapper = new MetalBuffer(buffer, size);
    }

    public IntPtr Buffer { get; }
    public int Size { get; }
    public IntPtr Contents { get; }
    public MetalBuffer Wrapper { get; }
    public int Used;
}

internal sealed class MetalBuffer : GpuBuffer
{
    public MetalBuffer(IntPtr handle, int length) : base(length) => Handle = handle;
    public IntPtr Handle { get; }

    // 上传块由设备的复用池持有并在设备释放时释放
    protected override void Release() { }
}

internal sealed class MetalTexture : GpuTexture
{
    readonly bool _owned;

    public MetalTexture(IntPtr handle, int width, int height, GpuTextureFormat format, GpuTextureUsage usage, bool owned)
        : base(width, height, format, usage)
    {
        Handle = handle;
        _owned = owned;
    }

    public IntPtr Handle { get; }

    protected override void Release()
    {
        if (_owned) ObjC.Release(Handle);
    }
}
